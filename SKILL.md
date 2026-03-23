---
name: remote-cmd
description: Remote command execution relay for AI agents. Execute PowerShell commands on remote Windows machines behind firewalls and NAT. Upload and download files to/from remote machines (max 200MB). Check remote machine connection status. Manage remote servers and workstations that are not directly accessible. Use this skill whenever the user mentions executing commands on remote machines, remote PowerShell execution, file upload or download to a remote machine, checking remote machine status, managing machines behind firewalls, NAT traversal for command execution, or accessing any machine through the RemoteCmd relay.
---

# Remote Command Execution Relay (remote-cmd)

## 1. Purpose & Context

**remote-cmd** is a three-component system that enables AI agents (such as Claude Code) to execute PowerShell commands and transfer files on remote Windows machines that sit behind firewalls or NAT. The remote machine does not need any inbound ports open -- it initiates all connections outbound to a relay server via HTTP polling.

**Why it exists:** Many target machines (servers, workstations, industrial PCs) sit behind corporate firewalls or consumer NAT routers with no inbound access. Traditional SSH or RDP requires port forwarding or VPN configuration on the target network. remote-cmd sidesteps this entirely: the client on the target machine polls outbound to a relay server, and controllers (Claude Code via MCP, or curl) send commands to that same relay. The relay bridges the two sides.

**Three components:**

| Component | Runtime | Role |
|-----------|---------|------|
| **RemoteCmd.Server** | .NET 9.0 (Kestrel) | HTTP relay on port 7890. Queues commands from controllers, serves them to polling clients, returns results. |
| **RemoteCmd.Client** | .NET 9.0 (self-contained exe) | Runs on the target machine. Polls the relay every 800ms for commands and file transfer requests. Executes via `powershell.exe`. |
| **mcp-server** | Node.js (MCP SDK) | STDIO-based MCP bridge. Translates Claude Code tool calls into HTTP requests against the relay server. |

## 2. Architecture

```
+---------------------+        +----------------------+        +---------------------+
|   Claude Code       |        |   Relay Server       |        |   Target Machine    |
|   (MCP Client)      |        |   (.NET 9, :7890)    |        |   (behind NAT)      |
|                     |        |                      |        |                     |
|  +---------------+  |  HTTP  |  /api/exec           |  HTTP  |  +---------------+  |
|  | MCP Server    |--+------->|  /api/upload         |<-------+--| Client        |  |
|  | (Node.js)     |  |        |  /api/download       | polling|  | (polling 800ms)|  |
|  | stdio         |  |        |  /api/status         |        |  |               |  |
|  +---------------+  |        |                      |        |  | PowerShell    |  |
+---------------------+        +----------------------+        +---------------------+
```

### Command Flow

1. Claude Code invokes an MCP tool (e.g., `remote_exec`).
2. MCP Server (Node.js) sends an HTTP POST to `/api/exec` on the Relay Server.
3. Relay Server queues the command. A `SemaphoreSlim(1)` enforces single-command-at-a-time.
4. Client on the target machine polls `/api/poll` every 800ms, picks up the encrypted command.
5. Client decrypts the command, executes it via `powershell.exe -NoProfile -NonInteractive`.
6. Client encrypts the result (stdout + stderr + exit code), POSTs it to `/api/result`.
7. Relay Server returns the result to the MCP Server, which returns it to Claude Code.

### File Transfer Flow

**Upload (local -> remote):**
1. Controller POSTs binary file data to `/api/upload?path=<remote_path>`.
2. Server stores file in memory, waits for client.
3. Client polls `/api/file-poll`, gets encrypted metadata (action, path, size).
4. Client GETs `/api/file-data`, receives encrypted file bytes.
5. Client decrypts, creates directories, writes file to disk.
6. Client POSTs `/api/file-done` to confirm.
7. Server returns success to controller.

**Download (remote -> local):**
1. Controller GETs `/api/download?path=<remote_path>`.
2. Server stores download request, waits for client.
3. Client polls `/api/file-poll`, gets encrypted metadata (action, path).
4. Client reads file from disk, encrypts, POSTs to `/api/file-upload`.
5. Server decrypts, returns binary to controller.
6. Controller (MCP Server) saves file locally.

### Encryption Model

All command payloads, results, file metadata, and file data are encrypted with **AES-256-GCM**:
- Key derivation: `SHA256("RemoteCmd:v1:" + token)` -> 256-bit key
- Nonce: 12 bytes, random per message
- Auth tag: 16 bytes (GCM integrity)
- Wire format: `nonce(12) + tag(16) + ciphertext(N)`

Transport layer optionally uses **TLS 1.2+** with an auto-generated self-signed certificate (RSA 2048, SHA256, 5-year validity). Disable with `--no-tls` flag.

## 3. Configuration

### Start the Relay Server

```bash
# With TLS (default)
dotnet run --project RemoteCmd.Server -- <TOKEN>

# Without TLS (HTTP only, AES encryption still active)
dotnet run --project RemoteCmd.Server -- <TOKEN> --no-tls
```

- Listens on `0.0.0.0:7890`
- If no token provided, a random 12-char token is generated

### Deploy the Client on Target Machine

```bash
# Build self-contained exe (no .NET runtime needed on target)
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/client

# Run on target machine
RemoteCmd.Client.exe <SERVER_IP_OR_URL> <TOKEN>

# Examples
RemoteCmd.Client.exe relay.example.com mySecretToken        # HTTPS (default)
RemoteCmd.Client.exe https://relay.example.com:7890 token   # Explicit HTTPS
RemoteCmd.Client.exe http://10.0.0.100:7890 token           # HTTP mode
```

### Configure MCP Server for Claude Code

Add to `.mcp.json` or Claude Code MCP settings:

```json
{
  "mcpServers": {
    "remote-cmd": {
      "type": "stdio",
      "command": "node",
      "args": ["C:/work/sources/remote-cmd/mcp-server/index.mjs"],
      "env": {
        "REMOTECMD_URL": "https://localhost:7890",
        "REMOTECMD_TOKEN": "<TOKEN>"
      }
    }
  }
}
```

**Environment variables:**

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `REMOTECMD_URL` | No | `https://localhost:7890` | Full URL of the relay server |
| `REMOTECMD_TOKEN` | Yes | `""` (empty) | Shared authentication token |

### Firewall / NAT

```powershell
# Windows Firewall on the relay server
netsh advfirewall firewall add rule name="RemoteCmd" dir=in action=allow protocol=tcp localport=7890
```

```
# MikroTik DST-NAT (if relay is behind NAT)
/ip firewall nat add chain=dstnat dst-port=7890 protocol=tcp \
  action=dst-nat to-addresses=<SERVER_LAN_IP> to-ports=7890
```

## 4. Complete MCP Tool Reference

### remote_exec

Execute a PowerShell command on the remote machine. Returns stdout, stderr, and exit code.

| Parameter | Type | Required | Default | Constraints | Description |
|-----------|------|----------|---------|-------------|-------------|
| `command` | string | Yes | -- | -- | PowerShell command to execute |
| `timeoutSeconds` | number | No | 30 | Max 300 | How long to wait for the command to complete |

**Returns** (JSON):
```json
{
  "output": "REMOTE-PC",
  "exitCode": 0
}
```

**Error responses (exitCode = -1):**

| output prefix | Meaning |
|--------------|---------|
| `[ERROR] No client connected` | Client has not polled in the last 10 seconds |
| `[ERROR] Another command is pending` | A command is already in progress (SemaphoreSlim timeout after 2s) |
| `[TIMEOUT] No response after Xs` | Command did not complete within timeoutSeconds |
| `[KILLED] Command exceeded 60s timeout` | Client-side process kill after 60 seconds |
| `[EXEC ERROR] ...` | PowerShell process failed to start or crashed |

**Execution details:**
- Shell: `powershell.exe -NoProfile -NonInteractive -Command "<command>"`
- Double quotes in the command are escaped automatically
- Output is combined stdout + stderr (stderr prefixed with `[STDERR]`)
- Only one command executes at a time (server-side semaphore)
- Client-side hard kill at 60 seconds regardless of timeoutSeconds

### remote_status

Check if the remote client is connected to the relay server. Takes no parameters.

| Parameter | Type | Required |
|-----------|------|----------|
| _(none)_ | -- | -- |

**Returns** (JSON):
```json
{
  "clientConnected": true,
  "lastPoll": "2026-02-11T14:20:18Z",
  "secondsAgo": 2,
  "encryption": "AES-256-GCM",
  "tls": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `clientConnected` | boolean | True if client polled within the last 10 seconds |
| `lastPoll` | string (ISO 8601) | UTC timestamp of the last client poll |
| `secondsAgo` | integer | Seconds since last poll (-1 if disconnected) |
| `encryption` | string | Always `"AES-256-GCM"` |
| `tls` | boolean | Whether TLS is enabled on the relay |

### remote_upload

Upload a file from the local machine to the remote machine. Maximum file size is 200MB.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `localPath` | string | Yes | Absolute path to the file on the local machine |
| `remotePath` | string | Yes | Absolute path where the file should be saved on the remote machine |

**Returns** (JSON on success):
```json
{
  "status": "ok",
  "size": 254976
}
```

**Error conditions:**
- Local file not found: returns `isError: true` with message
- No client connected: `{"error": "No client connected"}`
- Transfer timeout (5 minutes): `{"error": "Upload timeout"}`

**Notes:**
- Directories are created automatically on the remote machine
- File data is encrypted with AES-256-GCM in transit
- The MCP server reads the entire file into memory before sending

### remote_download

Download a file from the remote machine to the local machine. Maximum file size is 200MB.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `remotePath` | string | Yes | Absolute path to the file on the remote machine |
| `localPath` | string | Yes | Absolute path where the file should be saved locally |

**Returns** (JSON on success):
```json
{
  "status": "ok",
  "size": 102400,
  "localPath": "C:\\Users\\user\\Desktop\\remote-file.log"
}
```

**Error conditions:**
- File not found on remote: `{"error": "File not found: C:\\path\\to\\file"}`
- No client connected: `{"error": "No client connected"}`
- Transfer timeout (5 minutes): HTTP 504

**Notes:**
- Local directories are created automatically
- Existing local files are overwritten
- File data is encrypted with AES-256-GCM in transit

## 5. Workflow Recipes

### Check if the remote machine is connected

```
Use remote_status tool (no parameters).
If clientConnected is true, the machine is reachable.
If false, the client is not running or has lost connectivity.
```

### Run a diagnostic command

```
remote_exec with command: "hostname"
remote_exec with command: "Get-Process | Select-Object -First 10"
remote_exec with command: "Get-Service | Where-Object Status -eq Running"
remote_exec with command: "Get-CimInstance Win32_LogicalDisk | Select DeviceID, FreeSpace, Size"
remote_exec with command: "systeminfo | Select-String 'Total Physical Memory'"
```

### Deploy a file to the remote machine

```
1. remote_status  (verify client is connected)
2. remote_upload with localPath: "C:\local\app.zip", remotePath: "C:\Users\user\Desktop\app.zip"
3. remote_exec with command: "Expand-Archive -Path C:\Users\user\Desktop\app.zip -DestinationPath C:\Apps\myapp -Force"
```

### Retrieve a file from the remote machine

```
1. remote_status  (verify client is connected)
2. remote_download with remotePath: "C:\Logs\app.log", localPath: "C:\Users\user\Desktop\app.log"
```

### Complex multi-step operation

```
1. remote_status
2. remote_exec: "Stop-Service MyService"
3. remote_upload: deploy new binary
4. remote_exec: "Start-Service MyService"
5. remote_exec: "Get-Service MyService | Select Status"
```

### Long-running command

```
remote_exec with command: "chkdsk C: /scan", timeoutSeconds: 300
```

## 6. Security Considerations

- **Token-based authentication**: All API endpoints require a shared token via `?token=<TOKEN>` query parameter or `X-Token` header. Minimum 24 characters recommended for production use.
- **AES-256-GCM payload encryption**: All sensitive data (commands, results, file contents, file metadata) is encrypted even over HTTP. Key is derived from the shared token.
- **TLS transport**: Self-signed certificate auto-generated on server startup (RSA 2048, SHA256, 5-year validity). Client accepts self-signed certificates by default (certificate validation disabled). Use `--no-tls` for HTTP-only mode (payload encryption remains active).
- **No command sandboxing**: PowerShell execution is completely unrestricted. Any command the client process can run will be executed. There is no allowlist, denylist, or role-based access.
- **No path traversal protection**: File upload and download accept any absolute path. The only restriction is the OS file system permissions of the user running the client process.
- **No rate limiting**: The relay server does not limit request frequency. A compromised token allows unlimited command execution.
- **Single static token**: No token rotation, expiry, or multi-user support. Protect the token as you would a root password.
- **Token in query parameters**: When using `?token=` (vs. `X-Token` header), the token may appear in proxy logs, server access logs, and HTTP Referer headers.

## 7. Operational Details

| Parameter | Value | Notes |
|-----------|-------|-------|
| Client poll interval | 800ms | Continuous loop while connected |
| Command timeout (default) | 30 seconds | Configurable per request via `timeoutSeconds` |
| Command timeout (max) | 300 seconds | Server-side limit |
| Process kill timeout | 60 seconds | Client kills the PowerShell process after 60s regardless |
| File transfer timeout | 5 minutes | Both upload and download |
| Max file size | 200MB | Server request body limit and transfer cap |
| Max request body | 200MB | Kestrel `MaxRequestBodySize` setting |
| Client connection detection | 10 seconds | Client considered connected if last poll < 10s ago |
| Auto-reconnect backoff | 1s, 2s, 4s, 8s, 16s, 30s | Exponential, capped at 30 seconds |
| Command concurrency | 1 | SemaphoreSlim(1) on the server, 2s wait before rejecting |
| Shell | `powershell.exe -NoProfile -NonInteractive` | No user profile loaded, no interactive prompts |
| MCP transport | STDIO | MCP Server communicates with Claude Code via stdin/stdout |
| HTTP client timeout | 5 minutes (300s) | MCP Server HTTP request timeout |
| Server port | 7890 | Hardcoded in both server and client |
| Self-contained client exe | ~68MB | Includes .NET runtime, no dependencies on target |

## 8. Tips & Gotchas

- **Always check status first.** Before sending commands or file transfers, call `remote_status` to verify the client is connected. If `clientConnected` is false, all operations will fail with "No client connected".

- **One command at a time.** The relay enforces single-command concurrency. If you send a second command while the first is still executing, you get "Another command is pending". Wait for the first to complete or timeout.

- **Long-running commands need increased timeout.** The default is 30 seconds. For operations like `chkdsk`, `sfc /scannow`, large file copies, or software installations, set `timeoutSeconds` up to 300.

- **The 60-second hard kill is on the client side.** Even if you set `timeoutSeconds: 300`, the client will kill the PowerShell process after 60 seconds. The server timeout controls how long the server waits for a response; the client kill timeout is hardcoded at 60 seconds. For commands genuinely needing more than 60 seconds, consider launching them as background jobs.

- **File paths on the remote machine use Windows backslashes.** When specifying `remotePath`, use `C:\Users\user\file.txt`, not forward slashes.

- **Directories are created automatically.** Both upload (on remote) and download (locally) will create parent directories if they do not exist.

- **Large files are held in memory.** Both the relay server and MCP server buffer entire files in memory. A 200MB upload requires ~200MB RAM on the server side and ~200MB on the MCP server side.

- **Errors are returned as JSON, not HTTP error codes.** Most error conditions (client disconnected, timeout, killed) still return HTTP 200 with an error message in the JSON body and `exitCode: -1`.

- **Client reconnects automatically.** If the relay server restarts or the network drops, the client will retry with exponential backoff (1s to 30s). No manual intervention needed.

- **PowerShell profile is not loaded.** Commands run with `-NoProfile`, so custom aliases, functions, and profile scripts from the remote user's PowerShell profile are not available.

- **stderr is included in output.** If a command produces stderr output, it appears after `[STDERR]` in the combined output string.

- **The token is the only security boundary.** Anyone with the token has full PowerShell access and arbitrary file read/write on the target machine. Treat the token with the same care as a root password or SSH private key.
