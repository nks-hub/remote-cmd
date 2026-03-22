# CRITICAL SECURITY AUDIT REPORT

**Project:** RemoteCmd v1.0.0 - Remote Command Execution Relay
**Repository:** https://github.com/nks-hub/remote-cmd
**Audit Date:** 2026-03-22
**Auditor:** Security Audit (DevSecOps)
**Risk Classification:** EXTREMELY HIGH - Remote Code Execution System

---

## EXECUTIVE SUMMARY

RemoteCmd is a remote command execution relay system that allows AI agents (Claude Code) to execute arbitrary PowerShell commands and transfer files on remote machines through NAT/firewalls. The system consists of three components: a .NET 9 HTTP relay server, a .NET 9 polling client, and a Node.js MCP bridge.

**Overall Security Rating: CRITICAL - NOT SUITABLE FOR PRODUCTION**

The system has **23 identified vulnerabilities**, of which **8 are CRITICAL**, **7 are HIGH**, **5 are MEDIUM**, and **3 are LOW** severity. The fundamental architecture prioritizes convenience over security, creating a system where a single compromised token grants unrestricted remote code execution and arbitrary file system access with no audit trail, no access controls, and no sandboxing.

---

## VULNERABILITY INVENTORY

| # | Finding | Severity | CVSS 3.1 | CWE |
|---|---------|----------|-----------|-----|
| 1 | Unrestricted Command Execution (No Sandboxing) | CRITICAL | 10.0 | CWE-78 |
| 2 | Token in URL Query Parameters (Credential Exposure) | CRITICAL | 9.1 | CWE-598 |
| 3 | Hardcoded Token in Shell Script (Committed to Git) | CRITICAL | 9.8 | CWE-798 |
| 4 | TLS Certificate Validation Completely Disabled | CRITICAL | 9.0 | CWE-295 |
| 5 | No Path Traversal Protection in File Transfer | CRITICAL | 9.1 | CWE-22 |
| 6 | Weak Key Derivation (SHA256 instead of KDF) | CRITICAL | 8.1 | CWE-916 |
| 7 | Token Printed to Console / Stdout | CRITICAL | 7.5 | CWE-532 |
| 8 | Server Binds to 0.0.0.0 (All Interfaces) | CRITICAL | 8.6 | CWE-668 |
| 9 | No Rate Limiting on Any Endpoint | HIGH | 7.5 | CWE-770 |
| 10 | No Audit Logging of Command Execution | HIGH | 7.4 | CWE-778 |
| 11 | Single Static Token (No Rotation, No Expiry) | HIGH | 7.2 | CWE-613 |
| 12 | Constant-Time Comparison Not Used for Token | HIGH | 7.1 | CWE-208 |
| 13 | PFX Certificate Written to Disk Unprotected | HIGH | 6.5 | CWE-312 |
| 14 | Node.js TLS Globally Disabled via Environment | HIGH | 7.0 | CWE-295 |
| 15 | No Input Validation on Command Content | HIGH | 8.0 | CWE-20 |
| 16 | Unbounded Memory Allocation (200MB Request Body) | MEDIUM | 6.5 | CWE-400 |
| 17 | Race Conditions in Shared Mutable State | MEDIUM | 5.9 | CWE-362 |
| 18 | Error Messages Leak Internal Information | MEDIUM | 5.3 | CWE-209 |
| 19 | Self-Signed Certificate with Wildcard DNS SAN | MEDIUM | 5.0 | CWE-295 |
| 20 | No Client Identity Verification | MEDIUM | 6.0 | CWE-287 |
| 21 | MCP Server Input Schema Lacks Constraints | LOW | 3.7 | CWE-20 |
| 22 | Dependency Version Pinning Absent | LOW | 3.1 | CWE-1104 |
| 23 | No Health Check or Liveness Probe | LOW | 2.0 | CWE-693 |

---

## DETAILED FINDINGS

---

### FINDING 1: Unrestricted Command Execution - No Sandboxing

**Severity:** CRITICAL (CVSS 10.0)
**CWE:** CWE-78 (OS Command Injection)
**Location:** `RemoteCmd.Client/Program.cs`, lines 135-173

#### Description

The client executes **any** PowerShell command received from the server with absolutely no restrictions, filtering, or sandboxing. The `ExecuteCommand` function passes the command string directly to `powershell.exe`:

```csharp
process.StartInfo = new ProcessStartInfo
{
    FileName = "powershell.exe",
    Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
    // ...
};
```

The only "sanitization" is escaping double quotes by replacing `"` with `\"`. This is trivially bypassable because PowerShell has numerous alternative quoting and execution mechanisms.

#### Attack Vectors

1. **Full system compromise**: Any command can be executed - `Invoke-WebRequest` for data exfiltration, `New-Service` for persistence, `Add-LocalGroupMember` for privilege escalation.
2. **Quote escape bypass**: PowerShell single quotes `'`, here-strings `@"..."@`, backtick escapes, `-EncodedCommand` with Base64-encoded payloads, and `$()` subexpressions all bypass the double-quote replacement.
3. **Lateral movement**: The client runs with the privilege level of the user who started it. If run as Administrator (common on target machines), the attacker has full SYSTEM-level capability.
4. **Process inherits all environment**: The spawned PowerShell process inherits the client's full environment, including any credentials, API keys, or session tokens in environment variables.

#### Proof of Concept

```bash
# Bypass the quote escaping via single quotes and encoded command
curl -X POST "http://server:7890/api/exec?token=TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"command":"[System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(\"d2hvYW1p\")) | Invoke-Expression"}'

# Direct command - add admin user
curl -X POST "http://server:7890/api/exec?token=TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"command":"net user hacker P@ssw0rd123 /add; net localgroup Administrators hacker /add"}'
```

#### Recommendation

- Implement a command allowlist with regex patterns for permitted commands.
- Run PowerShell in Constrained Language Mode (`__PSLockdownPolicy`).
- Use PowerShell AppLocker or WDAC policies to restrict executable scripts.
- Create a dedicated low-privilege service account for the client.
- Implement command approval workflow for destructive operations.
- Log every command with full context before execution.

---

### FINDING 2: Token in URL Query Parameters (Credential Exposure)

**Severity:** CRITICAL (CVSS 9.1)
**CWE:** CWE-598 (Use of GET Request Method With Sensitive Query Strings)
**Location:** `RemoteCmd.Server/Program.cs` line 73, `RemoteCmd.Client/Program.cs` lines 22-27

#### Description

The authentication token is passed as a URL query parameter (`?token=<TOKEN>`) on every single request. This is a well-known anti-pattern because:

1. **URL logging**: Web servers, proxies, load balancers, CDNs, and WAFs routinely log full URLs including query strings. The token appears in every log entry.
2. **Browser history**: If any endpoint is accessed from a browser, the token is stored in browser history.
3. **Referer header leakage**: If the response contains any external resources, the full URL (including token) is sent in the `Referer` header.
4. **Network inspection**: Even with TLS, the URL (including query string) may be visible in proxy logs, NAT device logs, and firewall inspection logs. MikroTik routers (used in this deployment) can log HTTP URLs.
5. **Client-side caching**: URLs with tokens may be cached by HTTP caching layers.

The client pre-constructs all URLs with the token embedded:

```csharp
var pollUrl = $"{baseUrl}/api/poll?token={token}";
var resultUrl = $"{baseUrl}/api/result?token={token}";
// ... every URL has the token baked in
```

#### Recommendation

- Move the token to the `Authorization: Bearer <token>` HTTP header exclusively.
- Remove query parameter token support entirely.
- If backward compatibility is needed, deprecate query parameter auth with a warning log.

---

### FINDING 3: Hardcoded Token in Shell Script (Committed to Git)

**Severity:** CRITICAL (CVSS 9.8)
**CWE:** CWE-798 (Use of Hard-coded Credentials)
**Location:** `rcmd.sh`, line 3

#### Description

The shell helper script contains a hardcoded authentication token:

```bash
TOKEN="heslo123"
```

This file is committed to the public GitHub repository (`https://github.com/nks-hub/remote-cmd`). The token `heslo123` (Czech for "password123") is now permanently in the git history even if removed from the current HEAD. Additionally, it reveals the naming convention and likely weak token choices of the operator.

#### Impact

- Anyone with access to the repository has a valid (or likely valid) authentication token.
- The token value suggests weak token hygiene -- simple dictionary words are used.
- Git history preserves this credential permanently unless force-rewritten.

#### Recommendation

- **Immediately rotate the token** if `heslo123` was ever used in production.
- Remove the hardcoded token; use environment variables: `TOKEN="${REMOTECMD_TOKEN:?missing token}"`.
- Run `git filter-repo` or BFG Repo Cleaner to purge the credential from git history.
- Add a pre-commit hook that scans for hardcoded secrets (e.g., `gitleaks`, `trufflehog`).
- Consider the repository compromised -- any token that was ever committed should be rotated.

---

### FINDING 4: TLS Certificate Validation Completely Disabled

**Severity:** CRITICAL (CVSS 9.0)
**CWE:** CWE-295 (Improper Certificate Validation)
**Location:** `RemoteCmd.Client/Program.cs` lines 33-36, `mcp-server/index.mjs` line 18

#### Description

Both the client and MCP server completely disable TLS certificate validation:

**C# Client:**
```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
};
```

**Node.js MCP Server:**
```javascript
if (isHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
```

This renders the TLS layer completely useless for authentication and integrity purposes. While the self-signed certificate still provides encryption of data in transit, it provides zero protection against:

1. **Man-in-the-Middle (MITM) attacks**: An attacker on the network path can intercept all traffic, including the authentication token transmitted with every request.
2. **DNS spoofing / ARP poisoning**: An attacker can redirect the client to a rogue server and capture the token.
3. **Network-level credential theft**: Once the token is captured via MITM, the attacker has full remote command execution capability.

The Node.js `NODE_TLS_REJECT_UNAUTHORIZED = "0"` is particularly dangerous because it is set as a **global environment variable**, disabling TLS validation for ALL https requests in the entire Node.js process, not just connections to the relay server.

#### Recommendation

- Implement certificate pinning: export the server's self-signed certificate and verify its thumbprint on the client.
- At minimum, pin the certificate's public key hash (SPKI pinning).
- For the Node.js MCP server, use a per-request `rejectUnauthorized: false` with explicit CA certificate instead of the global environment variable.
- Consider using a proper CA (Let's Encrypt) if the server has a DNS name.

---

### FINDING 5: No Path Traversal Protection in File Transfer

**Severity:** CRITICAL (CVSS 9.1)
**CWE:** CWE-22 (Path Traversal)
**Location:** `RemoteCmd.Client/Program.cs` lines 77-121, `RemoteCmd.Server/Program.cs` lines 155-185

#### Description

The file upload and download endpoints accept arbitrary file paths with no validation or restriction:

**Server (upload endpoint):**
```csharp
var remotePath = req.Query["path"].FirstOrDefault();
// No validation -- directly passed to client
```

**Client (file save):**
```csharp
await File.WriteAllBytesAsync(meta.Path, fileData);
```

**Client (file read):**
```csharp
var fileData = await File.ReadAllBytesAsync(meta.Path);
```

There is zero path validation. An attacker with the token can:

1. **Read any file** on the target system: `C:\Windows\System32\config\SAM`, `C:\Users\*\AppData\*`, private keys, database files.
2. **Write to any location**: Overwrite system binaries, drop malware into startup folders, modify configuration files.
3. **Path traversal with relative paths**: `..\..\..\..\Windows\System32\cmd.exe` or UNC paths `\\attacker\share\payload.exe`.
4. **Symbolic link attacks**: Write to symlink targets.

#### Proof of Concept

```bash
# Read Windows SAM database (password hashes)
curl -o sam.bak "http://server:7890/api/download?token=TOKEN&path=C:\Windows\System32\config\SAM"

# Write to startup folder for persistence
curl -X POST "http://server:7890/api/upload?token=TOKEN&path=C:\Users\jakub.cerny\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\backdoor.bat" \
  --data-binary @backdoor.bat

# Access via UNC path (if SMB is available)
curl -o secrets.txt "http://server:7890/api/download?token=TOKEN&path=\\fileserver\share\secrets.txt"
```

#### Recommendation

- Implement a configurable allowed-paths whitelist (e.g., only `C:\RemoteCmd\transfers\`).
- Validate and canonicalize all paths using `Path.GetFullPath()` and verify they fall within allowed directories.
- Block UNC paths (`\\`), device paths (`\\.\`, `\\?\`), and path traversal sequences (`..`).
- Run the client under a dedicated user account with file system ACLs restricting access.

---

### FINDING 6: Weak Key Derivation Function

**Severity:** CRITICAL (CVSS 8.1)
**CWE:** CWE-916 (Use of Password Hash With Insufficient Computational Effort)
**Location:** `RemoteCmd.Server/Crypto.cs` and `RemoteCmd.Client/Crypto.cs`, lines 11-13

#### Description

The AES-256-GCM encryption key is derived from the shared token using a single SHA256 hash:

```csharp
public static void Init(string token)
{
    _key = SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:v1:" + token));
}
```

SHA256 is **not a key derivation function**. It is:

1. **Extremely fast**: Modern GPUs can compute billions of SHA256 hashes per second, making brute-force attacks against weak tokens trivially fast.
2. **No salting**: The static prefix `"RemoteCmd:v1:"` is not a cryptographic salt -- it is identical for every deployment. Rainbow tables can be precomputed.
3. **No iteration**: A single hash round provides no computational resistance against offline attacks.
4. **Token entropy is likely low**: Combined with the hardcoded `heslo123` example and the auto-generated fallback of `Guid.NewGuid().ToString("N")[..12]` (12 hex characters = 48 bits of entropy), the effective key space is small.

If an attacker captures encrypted traffic (possible due to disabled TLS validation), they can brute-force the token offline. With a 12-character hex token and GPU-accelerated SHA256, the entire 48-bit keyspace can be exhausted in **under 3 days** on consumer hardware.

#### Recommendation

- Replace SHA256 with a proper KDF: **HKDF** (for high-entropy tokens) or **Argon2id/PBKDF2** (for human-chosen passwords).
- Enforce minimum token entropy (at least 128 bits / 32 hex characters).
- Add a random per-session salt exchanged during connection handshake.
- Example with HKDF:
```csharp
var prk = HKDF.Extract(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token));
_key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.UTF8.GetBytes("RemoteCmd:v1:aes-key"));
```

---

### FINDING 7: Token Printed to Console / Stdout

**Severity:** CRITICAL (CVSS 7.5)
**CWE:** CWE-532 (Insertion of Sensitive Information into Log File)
**Location:** `RemoteCmd.Server/Program.cs`, lines 56, 61, 64

#### Description

The server prints the authentication token in plaintext to stdout on startup:

```csharp
Console.WriteLine($"Token: {token}");
Console.WriteLine($"  RemoteCmd.Client.exe <THIS_SERVER_IP> {token}");
Console.WriteLine($"  curl -X POST {protocol}://localhost:7890/api/exec?token={token} ...");
```

In production environments, stdout is typically captured by:
- Process managers (systemd journal, Windows Event Log, Task Scheduler logs)
- Container runtimes (Docker logs, Kubernetes pod logs)
- Monitoring systems (Datadog, Splunk agents, ELK stack)
- Terminal scrollback buffers
- Screen recording or session recording tools

The token is now persisted in multiple locations beyond the operator's control.

#### Recommendation

- Never print the full token. Print only a masked version: `Token: heslo****` (first 5 + mask).
- Store the token in a configuration file with restricted permissions (chmod 600).
- Provide a `--show-token` flag that must be explicitly set to print the full token.

---

### FINDING 8: Server Binds to All Interfaces (0.0.0.0)

**Severity:** CRITICAL (CVSS 8.6)
**CWE:** CWE-668 (Exposure of Resource to Wrong Sphere)
**Location:** `RemoteCmd.Server/Program.cs`, lines 16, 26

#### Description

The server unconditionally binds to `0.0.0.0:7890`, accepting connections from all network interfaces:

```csharp
builder.WebHost.UseUrls("https://0.0.0.0:7890");
```

While necessary for the client to connect from a remote network, the `/api/exec`, `/api/upload`, and `/api/download` controller endpoints are also exposed to the entire network. The comment in the code says these are "controller-facing: plaintext, localhost" but they are served on the same interface and port as the client-facing endpoints.

Combined with the static token authentication, this means anyone who can reach port 7890 and knows/guesses the token has full remote code execution capability on the target machine.

#### Recommendation

- Separate controller and client endpoints onto different ports or interfaces.
- Bind the controller API to `127.0.0.1` only (localhost).
- Expose only client-facing endpoints (`/api/poll`, `/api/result`, `/api/file-*`) on the external interface.
- Alternatively, implement IP-based access control lists.

---

### FINDING 9: No Rate Limiting on Any Endpoint

**Severity:** HIGH (CVSS 7.5)
**CWE:** CWE-770 (Allocation of Resources Without Limits or Throttling)
**Location:** All endpoints in `RemoteCmd.Server/Program.cs`

#### Description

There is no rate limiting on any endpoint. This enables:

1. **Token brute-force**: An attacker can attempt unlimited token values against any authenticated endpoint. With a 12-hex-character token (48 bits), this is feasible over the network.
2. **Denial of Service**: Flooding `/api/exec` will cause the `commandLock` semaphore to queue indefinitely. Flooding `/api/upload` with 200MB payloads will exhaust server memory.
3. **Client starvation**: Flooding `/api/poll` and `/api/file-poll` from a rogue client will prevent the legitimate client from receiving commands.
4. **Resource exhaustion**: Each upload holds up to 200MB in memory with no concurrent request limit.

#### Recommendation

- Implement per-IP rate limiting using ASP.NET Core's built-in rate limiting middleware.
- Apply strict limits on authentication failures (e.g., 5 failures per minute, then exponential backoff).
- Limit concurrent uploads to prevent memory exhaustion.
- Implement a lockout mechanism after N failed token attempts.

---

### FINDING 10: No Audit Logging of Command Execution

**Severity:** HIGH (CVSS 7.4)
**CWE:** CWE-778 (Insufficient Logging)
**Location:** `RemoteCmd.Server/Program.cs` (exec endpoint), `RemoteCmd.Client/Program.cs` (ExecuteCommand)

#### Description

The system executes arbitrary PowerShell commands on remote machines but maintains no persistent audit log. The only logging is `Console.WriteLine` for file transfers. There is **zero logging** of:

- Which commands were executed
- When they were executed
- What the results were
- Who initiated the execution (all callers use the same token)
- Failed authentication attempts
- File transfer paths and sizes

For a remote command execution system, this is an unacceptable gap. In the event of a security incident, there is no forensic trail to determine what was executed, when, or by whom.

#### Recommendation

- Implement structured logging (Serilog or NLog) writing to a persistent, append-only log file.
- Log every command execution with: timestamp, source IP, command text, exit code, execution duration.
- Log every file transfer with: timestamp, source IP, path, direction, size.
- Log every authentication attempt (success and failure) with source IP.
- Consider shipping logs to a remote SIEM for tamper resistance.
- Implement log rotation and retention policies.

---

### FINDING 11: Single Static Token with No Rotation or Expiry

**Severity:** HIGH (CVSS 7.2)
**CWE:** CWE-613 (Insufficient Session Expiration)
**Location:** `RemoteCmd.Server/Program.cs` line 11, `RemoteCmd.Client/Program.cs` line 15

#### Description

Authentication relies on a single shared token that:

- Never expires
- Cannot be rotated without restarting both server and client
- Is shared between the controller (Claude Code/curl) and the polling client
- Provides identical access to all endpoints (no role separation)
- Is the same token used to derive the encryption key (compromising the token compromises all encryption)

There is no concept of sessions, token refresh, or multi-user access control. If the token is compromised at any point in its lifetime, the attacker has permanent access until the operator manually restarts with a new token.

#### Recommendation

- Separate the controller token from the client polling token.
- Implement token expiry with automatic rotation.
- Use short-lived JWT tokens derived from the static secret for actual API calls.
- Separate the authentication token from the encryption key derivation material.
- Implement a revocation mechanism.

---

### FINDING 12: Token Comparison Vulnerable to Timing Attack

**Severity:** HIGH (CVSS 7.1)
**CWE:** CWE-208 (Observable Timing Discrepancy)
**Location:** `RemoteCmd.Server/Program.cs`, line 75

#### Description

The token comparison uses the standard string inequality operator:

```csharp
if (reqToken != token)
```

Standard string comparison in .NET short-circuits on the first different character. An attacker can measure response time differences to determine the correct token character-by-character, reducing the brute-force space from `O(n^k)` to `O(n*k)`.

While timing attacks over the network require many samples and low-jitter connections, they are well-documented and practical in local network environments -- which is exactly how this system is deployed (via VPN/LAN).

#### Recommendation

- Use `CryptographicOperations.FixedTimeEquals()` for token comparison:
```csharp
var reqBytes = Encoding.UTF8.GetBytes(reqToken ?? "");
var tokenBytes = Encoding.UTF8.GetBytes(token);
if (!CryptographicOperations.FixedTimeEquals(reqBytes, tokenBytes))
```

---

### FINDING 13: PFX Certificate Written to Disk Unprotected

**Severity:** HIGH (CVSS 6.5)
**CWE:** CWE-312 (Cleartext Storage of Sensitive Information)
**Location:** `RemoteCmd.Server/Program.cs`, lines 22-24

#### Description

The auto-generated TLS certificate and its private key are exported to a PFX file on disk:

```csharp
var certPath = Path.Combine(AppContext.BaseDirectory, "remotecmd.pfx");
var certPassword = Guid.NewGuid().ToString("N")[..16];
File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, certPassword));
```

Issues:
1. The PFX file is written to the application's base directory with default file permissions (world-readable on many configurations).
2. The PFX password is generated in-memory and never persisted -- but the file persists across restarts.
3. Old PFX files are never cleaned up (new ones are generated each start, potentially overwriting).
4. If an attacker gains read access to the file system, they can extract the server's private key.

#### Recommendation

- Use an in-memory certificate without writing to disk (pass the `X509Certificate2` object directly to Kestrel).
- If disk persistence is needed, set restrictive file permissions (ACL for owner-only on Windows).
- Delete the PFX file after loading into Kestrel.
- Clean up stale PFX files on startup.

---

### FINDING 14: Node.js TLS Globally Disabled via Environment Variable

**Severity:** HIGH (CVSS 7.0)
**CWE:** CWE-295 (Improper Certificate Validation)
**Location:** `mcp-server/index.mjs`, line 18

#### Description

```javascript
if (isHttps) process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
```

This sets a **global** environment variable that disables TLS certificate validation for **all** HTTPS connections made by the Node.js process, not just connections to the RemoteCmd server. If the MCP server ever makes HTTPS calls to other services (logging, metrics, external APIs), those connections will also be vulnerable to MITM attacks.

#### Recommendation

- Use per-request TLS options instead of the global environment variable:
```javascript
const agent = new https.Agent({ rejectUnauthorized: false, ca: [serverCertPem] });
// pass agent in request options
```
- Better: implement certificate pinning by distributing the server's certificate to the MCP server.

---

### FINDING 15: No Input Validation on Command Content

**Severity:** HIGH (CVSS 8.0)
**CWE:** CWE-20 (Improper Input Validation)
**Location:** `RemoteCmd.Server/Program.cs` lines 114-151, `mcp-server/index.mjs` lines 236-249

#### Description

The command execution endpoint accepts any string as a command with no validation:

```csharp
var body = await req.ReadFromJsonAsync<CommandRequest>();
if (body?.Command == null)
    return Results.BadRequest(new { error = "Missing command" });
// Command is immediately queued for execution -- no validation
```

The MCP server also passes the command through without validation:

```javascript
const result = await apiCall("POST", "/api/exec", {
    command: args.command,
    timeoutSeconds: args.timeoutSeconds || 30,
});
```

There is no:
- Maximum command length limit (could be used for memory exhaustion)
- Character validation (null bytes, control characters)
- Command blocklist (e.g., `Format-Volume`, `Remove-Item -Recurse C:\`, `Stop-Computer`)
- Complexity limits

#### Recommendation

- Implement a maximum command length (e.g., 8192 characters).
- Validate that the command contains only printable characters.
- Implement a configurable command blocklist for destructive operations.
- Consider a command allowlist for high-security deployments.
- Add `timeoutSeconds` upper bound validation on the server (currently accepts any value).

---

### FINDING 16: Unbounded Memory Allocation

**Severity:** MEDIUM (CVSS 6.5)
**CWE:** CWE-400 (Uncontrolled Resource Consumption)
**Location:** `RemoteCmd.Server/Program.cs` lines 8, 29, 165-167

#### Description

The server allows request bodies up to 200MB and reads them entirely into memory:

```csharp
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);
// ...
using var ms = new MemoryStream();
await req.Body.CopyToAsync(ms);
var data = ms.ToArray(); // 200MB allocation
```

Multiple concurrent 200MB uploads would exhaust server memory rapidly. The encrypted data is also held in memory simultaneously (doubling the memory footprint during file transfer). There is no concurrent request limit.

#### Recommendation

- Implement streaming file transfer instead of loading entirely into memory.
- Add a concurrent upload limit (e.g., 1 simultaneous upload).
- Reduce the maximum file size or implement chunked transfers.
- Monitor and limit total server memory usage.

---

### FINDING 17: Race Conditions in Shared Mutable State

**Severity:** MEDIUM (CVSS 5.9)
**CWE:** CWE-362 (Race Condition)
**Location:** `RemoteCmd.Server/Program.cs`, lines 42-51

#### Description

Multiple shared variables are accessed without synchronization:

```csharp
string? pendingCommand = null;
TaskCompletionSource<CommandResult>? resultTcs = null;
DateTime lastClientPoll = DateTime.MinValue;
FileTransfer? pendingUpload = null;
TaskCompletionSource<bool>? uploadTcs = null;
FileTransfer? pendingDownload = null;
TaskCompletionSource<FileTransfer>? downloadTcs = null;
```

While `commandLock` protects the exec path, the file transfer state variables (`pendingUpload`, `uploadTcs`, `pendingDownload`, `downloadTcs`, `lastClientPoll`) are accessed from multiple concurrent request handlers with no synchronization. This can lead to:

1. **Lost file transfers**: Concurrent upload requests can overwrite each other's state.
2. **Null reference exceptions**: `uploadTcs` or `downloadTcs` can be set to null between a null check and usage.
3. **Data corruption**: Reading partial state during concurrent writes.

#### Recommendation

- Protect all shared state with a lock or use `Interlocked` operations.
- Use a proper queue/channel for pending operations instead of single-slot variables.
- Consider using `System.Threading.Channels` for thread-safe producer-consumer patterns.

---

### FINDING 18: Error Messages Leak Internal Information

**Severity:** MEDIUM (CVSS 5.3)
**CWE:** CWE-209 (Generation of Error Message Containing Sensitive Information)
**Location:** `RemoteCmd.Client/Program.cs` lines 104, 119

#### Description

Error messages from the client are sent back to the server and ultimately to the controller, potentially leaking internal information:

```csharp
// File not found -- leaks full internal path
await http.PostAsync($"{fileUploadUrl}&error=File not found: {meta.Path}", null);

// Exception message -- may contain stack traces, internal paths, permission details
await http.PostAsync($"{fileUploadUrl}&error={ex.Message}", null);
```

The error message is passed as a URL query parameter, compounding the issue (logged in URL logs). Exception messages from .NET can contain:
- Full file system paths
- User account names
- Permission details
- Stack trace information
- Internal IP addresses

#### Recommendation

- Return generic error codes instead of exception messages.
- Log detailed errors locally on the client; send only safe error identifiers to the server.
- Never pass error details in URL query parameters.

---

### FINDING 19: Self-Signed Certificate with Wildcard DNS SAN

**Severity:** MEDIUM (CVSS 5.0)
**CWE:** CWE-295 (Improper Certificate Validation)
**Location:** `RemoteCmd.Server/Program.cs`, lines 323-327

#### Description

The self-signed certificate includes a wildcard DNS SAN:

```csharp
var sanBuilder = new SubjectAlternativeNameBuilder();
sanBuilder.AddDnsName("localhost");
sanBuilder.AddDnsName("*");        // Wildcard for ANY hostname
```

The `*` DNS SAN means this certificate is valid for any hostname. If this certificate were ever trusted by a client (e.g., imported into a trust store), it would validate connections to any server. Combined with the disabled TLS validation, this creates a situation where certificate trust decisions are meaningless.

Additionally, the certificate validity of 5 years (line 331-332) far exceeds best practices (90 days for Let's Encrypt, 398 days maximum for public CAs).

#### Recommendation

- Use specific DNS names and IP addresses in the SAN, not wildcards.
- Reduce certificate validity to 1 year or less.
- Generate a unique certificate per deployment with the actual server hostname.

---

### FINDING 20: No Client Identity Verification

**Severity:** MEDIUM (CVSS 6.0)
**CWE:** CWE-287 (Improper Authentication)
**Location:** `RemoteCmd.Server/Program.cs`, lines 87-98

#### Description

Any entity that knows the token can act as the polling client. The server has no way to distinguish between the legitimate client and a rogue client. A rogue client could:

1. **Intercept commands**: Poll faster than the real client and steal pending commands (which are cleared after one poll).
2. **Send fake results**: Submit fabricated command results to the controller.
3. **Poison file transfers**: Intercept upload data, modify it, and confirm completion.
4. **Denial of service**: Constantly poll and clear pending commands before the real client gets them.

The `lastClientPoll` timestamp is updated by any authenticated poll request, so a rogue client also masks the real client's disconnection.

#### Recommendation

- Implement client identity binding (e.g., client generates a keypair, server validates the client's public key).
- Use mutual TLS (mTLS) for client authentication.
- Add a client registration/handshake step that binds a session to a specific client identifier.
- Detect and alert on concurrent client connections.

---

### FINDING 21: MCP Server Input Schema Lacks Constraints

**Severity:** LOW (CVSS 3.7)
**CWE:** CWE-20 (Improper Input Validation)
**Location:** `mcp-server/index.mjs`, lines 158-229

#### Description

The MCP tool input schemas define required fields but no constraints:

```javascript
command: {
    type: "string",
    description: "PowerShell command to execute on remote machine",
    // No maxLength, no pattern, no enum constraints
},
timeoutSeconds: {
    type: "number",
    description: "Timeout in seconds (default 30, max 300)",
    default: 30,
    // No minimum, no maximum enforced in schema
},
```

The schema claims "max 300" in the description but does not enforce it. The `localPath` and `remotePath` fields have no format validation.

#### Recommendation

- Add `maxLength` to string fields.
- Add `minimum` and `maximum` to numeric fields.
- Add `pattern` constraints for paths (e.g., must start with a drive letter on Windows).
- Validate inputs in the handler before forwarding to the API.

---

### FINDING 22: Dependency Version Pinning Absent

**Severity:** LOW (CVSS 3.1)
**CWE:** CWE-1104 (Use of Unmaintained Third Party Components)
**Location:** `mcp-server/package.json`

#### Description

The MCP server's single dependency uses a caret range:

```json
"dependencies": {
    "@modelcontextprotocol/sdk": "^1.0.0"
}
```

This allows automatic installation of any minor/patch version >= 1.0.0 and < 2.0.0. Combined with:
- No `package-lock.json` (it is in `.gitignore`)
- No integrity hashes
- No SBOM (Software Bill of Materials)

This creates supply chain risk. A compromised version of the MCP SDK could be installed silently.

The .NET projects have no external NuGet dependencies (good), relying only on the framework.

#### Recommendation

- Pin exact dependency versions: `"@modelcontextprotocol/sdk": "1.0.0"`.
- Commit `package-lock.json` to the repository (remove from `.gitignore`).
- Run `npm audit` in CI/CD.
- Generate an SBOM for the project.
- Consider using Snyk or Dependabot for automated vulnerability scanning.

---

### FINDING 23: No Health Check or Liveness Probe

**Severity:** LOW (CVSS 2.0)
**CWE:** CWE-693 (Protection Mechanism Failure)
**Location:** `RemoteCmd.Server/Program.cs`

#### Description

The server has no dedicated health check endpoint. The root `/` endpoint returns a static text banner but does not verify internal state (e.g., whether the semaphore is deadlocked, whether memory is exhausted). There is no mechanism to detect a hung server or trigger automatic restart.

#### Recommendation

- Add a `/health` endpoint that verifies internal state.
- Include checks for: memory usage, semaphore availability, time since last client poll.
- Use the health check in container orchestration or process managers for automatic restart on failure.

---

## ARCHITECTURAL SECURITY CONCERNS

### 1. Single Point of Failure / Single Point of Compromise

The entire security model depends on a single shared secret (the token). This token simultaneously serves as:
- Authentication credential (API access)
- Encryption key material (AES-256-GCM key derivation)
- Client identity proof
- Controller authorization

Compromising this one value grants: full remote code execution, arbitrary file read/write, decryption of all captured traffic, and the ability to impersonate the client.

### 2. No Defense in Depth

There is exactly one security control (token authentication). There are no backup controls:
- No IP-based access control
- No mutual TLS
- No command authorization layer
- No file path restrictions
- No process isolation
- No network segmentation enforcement
- No intrusion detection

### 3. Encryption Without Authentication Binding

The AES-256-GCM encryption is derived from the same token used for authentication. This means:
- Authentication and encryption are not independently revocable.
- Rotating the encryption key requires changing the authentication token (and vice versa).
- Captured encrypted traffic can be decrypted by anyone who later obtains the token.

### 4. Polling Architecture Concerns

The 800ms polling interval means:
- Commands sit in server memory in plaintext (as `pendingCommand`) for up to 800ms.
- There is no guarantee of delivery (if the client crashes between poll and result submission).
- The server has no persistent queue -- commands are lost on server restart.

---

## THREAT MODEL (STRIDE Analysis)

| Threat | Category | Risk | Current Mitigation |
|--------|----------|------|--------------------|
| Attacker intercepts token via MITM | Spoofing | CRITICAL | None (TLS validation disabled) |
| Rogue client steals commands | Spoofing | HIGH | None |
| Attacker brute-forces token | Spoofing | HIGH | None (no rate limiting) |
| Attacker modifies command in transit | Tampering | MEDIUM | AES-GCM integrity (if key not compromised) |
| Attacker modifies file during transfer | Tampering | MEDIUM | AES-GCM integrity (if key not compromised) |
| Token appears in logs/history | Information Disclosure | CRITICAL | None (token in URLs, console output) |
| File contents exfiltrated via download | Information Disclosure | CRITICAL | None (no path restrictions) |
| Server memory exhausted via uploads | Denial of Service | HIGH | 200MB limit per request only |
| Polling endpoint flooded | Denial of Service | HIGH | None |
| Attacker executes privileged commands | Elevation of Privilege | CRITICAL | None (no sandboxing) |
| Attacker writes to privileged paths | Elevation of Privilege | CRITICAL | None (no path restrictions) |
| No forensic trail after compromise | Repudiation | HIGH | None (no audit logging) |

---

## COMPLIANCE ASSESSMENT

| Framework | Status | Key Gaps |
|-----------|--------|----------|
| OWASP ASVS L1 | FAIL | Missing V2 (Authentication), V3 (Session), V5 (Validation), V7 (Logging), V8 (Data Protection), V9 (Communication) |
| NIST SP 800-53 | FAIL | AC-3 (Access Enforcement), AU-2 (Audit Events), IA-5 (Authenticator Management), SC-8 (Transmission Confidentiality) |
| CIS Controls v8 | FAIL | Control 3 (Data Protection), Control 5 (Account Management), Control 6 (Access Control), Control 8 (Audit Log Management) |
| PCI-DSS | N/A | Not applicable unless processing payment data, but would fail on all relevant controls |

---

## PRIORITIZED REMEDIATION PLAN

### Phase 1: IMMEDIATE (Fix within 24 hours)

1. **Remove hardcoded token from `rcmd.sh`** and purge from git history.
2. **Rotate any token that was ever committed** to the repository.
3. **Move token from URL query parameters to Authorization header**.
4. **Implement constant-time token comparison** using `CryptographicOperations.FixedTimeEquals()`.
5. **Stop printing the full token to console**.

### Phase 2: URGENT (Fix within 1 week)

6. **Implement audit logging** for all command executions, file transfers, and auth attempts.
7. **Add rate limiting** on all endpoints, especially authentication.
8. **Implement path validation** for file transfers (allowlist of permitted directories).
9. **Add basic command validation** (length limits, character validation, optional blocklist).
10. **Fix TLS certificate validation** -- implement certificate pinning.

### Phase 3: IMPORTANT (Fix within 1 month)

11. **Replace SHA256 key derivation with HKDF** and enforce minimum token entropy.
12. **Separate controller and client authentication** (different tokens/roles).
13. **Add client identity binding** (mTLS or public key registration).
14. **Implement PowerShell sandboxing** (Constrained Language Mode, dedicated service account).
15. **Fix race conditions** in shared state with proper synchronization.
16. **Remove global `NODE_TLS_REJECT_UNAUTHORIZED`** in MCP server.

### Phase 4: HARDENING (Ongoing)

17. **Separate controller API to localhost-only binding**.
18. **Implement command approval workflow** for destructive operations.
19. **Add health checks and monitoring**.
20. **Pin dependency versions and commit lockfiles**.
21. **Generate SBOM and set up automated vulnerability scanning**.
22. **Implement session management with token expiry and rotation**.
23. **Consider replacing polling with WebSocket for lower latency and better security properties**.

---

## CONCLUSION

RemoteCmd is a functional tool for its intended use case (AI agent remote command execution through NAT), but it was built with a **"make it work first"** mindset that prioritized functionality over security. For internal use on trusted networks with a single operator, the risk may be acceptable with awareness. However, the system in its current state:

- Must **NEVER** be exposed to the public internet without the mitigations listed above.
- Must **NEVER** be used in environments with compliance requirements.
- Should be treated as having the **same trust level as an open SSH session** with the target machine, because that is functionally what it provides.

The most dangerous aspect is the combination of remote code execution capability with weak authentication and zero auditing. An attacker who obtains the token (via git history, log files, MITM, or brute force) gains silent, unrestricted access to the target machine with no forensic trail.

**The public GitHub repository already contains a plaintext token (`heslo123`) in `rcmd.sh`. Assume this token is compromised. If it was ever used in production, the target machine should be considered potentially compromised.**

---

*End of Security Audit Report*
