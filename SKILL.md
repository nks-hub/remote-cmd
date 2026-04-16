---
name: remote-cmd
description: Remote command execution relay for AI agents. Execute PowerShell commands on remote Windows machines behind firewalls and NAT. Upload and download files to/from remote machines (max 200MB). Check remote machine connection status. Manage multiple remote servers and workstations through a single relay — list connected clients, target a specific machine by name, and switch between them. Use this skill whenever the user mentions executing commands on remote machines, remote PowerShell execution, file upload or download to a remote machine, checking remote machine status, managing machines behind firewalls, NAT traversal for command execution, selecting between multiple connected clients, or accessing any machine through the RemoteCmd relay.
---

# Remote Command Execution Relay (remote-cmd)

## 1. Purpose & Context

**remote-cmd** is a three-component system that enables AI agents (such as Claude Code) to execute PowerShell commands and transfer files on remote Windows machines that sit behind firewalls or NAT. The remote machines do not need any inbound ports open -- they initiate all connections outbound to a relay server via HTTP polling.

**Why it exists:** Many target machines (servers, workstations, industrial PCs) sit behind corporate firewalls or consumer NAT routers with no inbound access. Traditional SSH or RDP requires port forwarding or VPN configuration on the target network. remote-cmd sidesteps this entirely: the client on each target machine polls outbound to a relay server, and controllers (Claude Code via MCP, or curl) send commands to that same relay. The relay bridges the two sides.

**Multi-client (v1.1.0+):** A single relay can serve many target machines simultaneously. Each client registers with a stable `clientId` (persisted to disk) and a human-readable `name`. Controllers target a specific machine via the `client` parameter (name or id). When only one client is connected, the `client` parameter is optional.

**Three components:**

| Component | Runtime | Role |
|-----------|---------|------|
| **RemoteCmd.Server** | .NET 9.0 (Kestrel) | HTTP relay on port 7890. Manages a dictionary of client sessions. |
| **RemoteCmd.Client** | .NET 9.0 (self-contained exe) | Runs on each target machine. Polls the relay every 800 ms. Executes via `powershell.exe`. |
| **mcp-server** | Node.js (MCP SDK) | STDIO-based MCP bridge. Translates tool calls into HTTP requests against the relay. |

## 2. Architecture

```
+---------------------+        +----------------------+        +---------------------+
|   Claude Code       |        |   Relay Server       |        |   Target Machines   |
|   (MCP Client)      |        |   (.NET 9, :7890)    |        |   (behind NAT)      |
|                     |        |                      |        |                     |
|  +---------------+  |  HTTP  |  /api/exec           |  HTTP  |  +---------------+  |
|  | MCP Server    |--+------->|  /api/upload         |<-------+--| Client A      |  |
|  | (Node.js)     |  |        |  /api/download       | polling|  +---------------+  |
|  | stdio         |  |        |  /api/clients        |<-------+--| Client B ...  |  |
|  +---------------+  |        |  /api/status         |        |  +---------------+  |
+---------------------+        +----------------------+        +---------------------+
```

### Command Flow

1. Claude Code invokes an MCP tool (e.g., `remote_exec` with `{command: "hostname", client: "machine-a"}`).
2. MCP Server sends an HTTP POST to `/api/exec?client=machine-a` on the Relay Server.
3. Relay resolves the target session:
   - If `client` specified → find session by name or id (404 if unknown, 400 if not connected).
   - If omitted and exactly one client connected → auto-select.
   - If omitted and multiple connected → error listing connected client names.
4. Relay queues the command on that session. Per-session `SemaphoreSlim(1)` enforces single-command-at-a-time per client (different clients run in parallel).
5. Client A polls `/api/poll?clientId=<guid>&name=machine-a`, picks up the encrypted command.
6. Client executes via `powershell.exe -NoProfile -NonInteractive`.
7. Client POSTs encrypted result to `/api/result?clientId=<guid>`.
8. Relay returns the result to the MCP Server, which returns it to Claude Code.

### File Transfer Flow

Upload and download follow the same session-scoped pattern. Each client has its own `PendingUpload` / `PendingDownload` state — transfers to different clients run concurrently without interfering.

### Encryption Model

All command payloads, results, file metadata, and file data are encrypted with **AES-256-GCM**:
- Key derivation: `SHA256("RemoteCmd:v1:" + token)` -> 256-bit key
- Nonce: 12 bytes, random per message
- Auth tag: 16 bytes (GCM integrity)
- Wire format: `nonce(12) + tag(16) + ciphertext(N)`

Transport layer optionally uses **TLS 1.2+** with an auto-generated self-signed certificate. Disable with `--no-tls` flag or `REMOTECMD_NO_TLS=1`.

## 3. Configuration

### Start the Relay Server

```bash
# With TLS (default)
dotnet run --project RemoteCmd.Server -- <TOKEN>

# HTTP only
dotnet run --project RemoteCmd.Server -- <TOKEN> --no-tls

# Via env vars (systemd, containers, tests)
REMOTECMD_TOKEN=<TOKEN> REMOTECMD_NO_TLS=1 dotnet run --project RemoteCmd.Server
```

### Deploy Clients

```bash
# Build self-contained exe
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/client

# Run on each target machine with a distinctive name
RemoteCmd.Client.exe <SERVER_IP> <TOKEN> --name comos-1
RemoteCmd.Client.exe <SERVER_IP> <TOKEN> --name build-server
RemoteCmd.Client.exe <SERVER_IP> <TOKEN>                 # default: %COMPUTERNAME%
```

Each client persists its GUID to `%LOCALAPPDATA%\RemoteCmd\client.id` so the id is stable across restarts.

### MCP Server Configuration

```json
{
  "mcpServers": {
    "remote-cmd": {
      "type": "stdio",
      "command": "node",
      "args": ["C:/work/sources/remote-cmd/mcp-server/index.mjs"],
      "env": {
        "REMOTECMD_URL": "https://localhost:7890",
        "REMOTECMD_TOKEN": "<TOKEN>",
        "REMOTECMD_DEFAULT_CLIENT": "comos-1"
      }
    }
  }
}
```

**Environment variables:**

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `REMOTECMD_URL` | No | `https://localhost:7890` | Full URL of the relay server |
| `REMOTECMD_TOKEN` | Yes | — | Shared authentication token |
| `REMOTECMD_DEFAULT_CLIENT` | No | — | Default client name/id when `client` tool argument is omitted |

## 4. Complete MCP Tool Reference

### remote_list_clients

List every client the relay knows about. No parameters.

**Returns** (JSON):
```json
{
  "count": 2,
  "connected": 2,
  "clients": [
    {"id": "a3f1...", "name": "comos-1", "lastPoll": "2026-04-16T10:00:00Z", "secondsAgo": 1, "connected": true},
    {"id": "b7c2...", "name": "build-server", "lastPoll": "2026-04-16T10:00:02Z", "secondsAgo": 0, "connected": true}
  ]
}
```

**Always call this first when there could be more than one target machine.**

### remote_status

Check connection status. Without `client` returns aggregate; with `client` returns per-client details.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `client` | string | No | Target client name or id. If omitted, returns aggregate. |

**Aggregate response:**
```json
{"clientConnected": true, "totalClients": 2, "connectedClients": 2, "encryption": "AES-256-GCM", "tls": true}
```

**Per-client response:**
```json
{"clientConnected": true, "name": "comos-1", "id": "a3f1...", "lastPoll": "2026-04-16T10:00:00Z", "secondsAgo": 1, "encryption": "AES-256-GCM", "tls": true}
```

### remote_exec

Execute a PowerShell command on a target client.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `command` | string | Yes | — | PowerShell command to execute |
| `timeoutSeconds` | number | No | 30 | Server-side wait timeout (max 300) |
| `client` | string | No | auto | Target client name or id |

**Target selection** (when `client` is omitted):
1. If `REMOTECMD_DEFAULT_CLIENT` env is set → that client.
2. Else if exactly one client is connected → that one.
3. Else → error: `[ERROR] Multiple clients connected (name-a, name-b); specify ?client=<name|id>`.

**Error prefixes on exitCode = -1:**

| Prefix | Meaning |
|-------|---------|
| `[ERROR] No client connected` | No sessions polled in the last 10 s |
| `[ERROR] Unknown client '<x>'` | No session with that name or id |
| `[ERROR] Client '<x>' not connected` | Session exists but lastPoll > 10 s ago |
| `[ERROR] Multiple clients connected (...)` | Omitted `client` but 2+ connected |
| `[ERROR] Another command is pending on '<x>'` | That client is busy |
| `[TIMEOUT] No response from '<x>' after Xs` | Command did not complete |

### remote_upload

Upload a local file to a target client (max 200MB).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `localPath` | string | Yes | Absolute path to the file on the local machine |
| `remotePath` | string | Yes | Absolute path on the remote machine |
| `client` | string | No | Target client name or id |

### remote_download

Download a file from a target client to the local machine (max 200MB).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `remotePath` | string | Yes | Absolute path on the remote machine |
| `localPath` | string | Yes | Absolute path on the local machine |
| `client` | string | No | Target client name or id |

## 5. Workflow Recipes

### See what machines are available

```
remote_list_clients            (no args)
```

### Run a command on the default target

```
remote_exec command:"hostname"
```

If multiple clients are connected and no default is set, this returns an error listing the connected names.

### Run a command on a specific machine

```
remote_exec command:"Get-Service -Name MyService" client:"comos-1"
```

### Switch between clients across a session

```
remote_list_clients                                          (discover)
remote_exec client:"comos-1"  command:"hostname"             (A reports hostname)
remote_exec client:"build-srv" command:"Get-Service"         (B reports services)
remote_upload  client:"build-srv" localPath:"..."  remotePath:"C:\drop\app.zip"
remote_exec    client:"build-srv" command:"Expand-Archive C:\drop\app.zip -DestinationPath C:\Apps\myapp -Force"
```

### Deploy the same artifact to multiple machines

```
remote_list_clients                                         (list all)
# loop over clients, skipping any with connected=false:
remote_upload client:"comos-1"    localPath:"..." remotePath:"C:\dest\app.zip"
remote_upload client:"comos-2"    localPath:"..." remotePath:"C:\dest\app.zip"
remote_upload client:"build-srv"  localPath:"..." remotePath:"C:\dest\app.zip"
```

Because each client has its own `SemaphoreSlim`, uploads to different clients run in parallel on the server — but note that the MCP tool calls themselves are issued serially by Claude Code.

### Verify connectivity before a long operation

```
remote_status client:"comos-1"
# if clientConnected == false → client is down, do not proceed.
remote_exec client:"comos-1" command:"chkdsk C: /scan" timeoutSeconds:300
```

### Long-running command

```
remote_exec command:"chkdsk C: /scan" timeoutSeconds:300 client:"comos-1"
```

The client still has a hard-coded 60 s process kill. For anything over 60 s, launch as a background job: `Start-Job { chkdsk C: /scan }` and poll for completion.

## 6. Security Considerations

- **Token-based authentication** on every `/api/*` endpoint, accepted via `?token=<T>`, `X-Token: <T>` header, or `Authorization: Bearer <T>`. Constant-time comparison used server-side. Prefer header/Bearer over query string in production — query strings leak into proxy logs.
- **AES-256-GCM payload encryption** on commands, results, file contents, and file metadata. Key derived from the shared token (`SHA256("RemoteCmd:v1:" + token)`).
- **TLS transport** — self-signed certificate auto-generated on server startup. Client accepts self-signed certs.
- **No command sandboxing** — PowerShell execution is unrestricted. Any command the client process can run will be executed.
- **No path traversal protection** — upload/download accept any absolute path.
- **No rate limiting** — a compromised token allows unlimited command execution.
- **Single shared token** — no rotation, expiry, or per-client auth. Every client knows the same token.
- **Stable client id persisted to disk** — if the disk is compromised, the id can be replayed but the attacker still needs the token.

## 7. Operational Details

| Parameter | Value | Notes |
|-----------|-------|-------|
| Client poll interval | 800 ms | Continuous loop while connected |
| Command timeout (default) | 30 s | Configurable via `timeoutSeconds` |
| Command timeout (max) | 300 s | Server-side cap |
| Process kill timeout | 60 s | Client kills PowerShell regardless |
| File transfer timeout | 5 min | Upload and download |
| Max file / body size | 200 MB | Kestrel `MaxRequestBodySize` |
| Client connection detection | 10 s | Connected if lastPoll < 10 s ago |
| Auto-reconnect backoff | 1, 2, 4, 8, 16, 30 s | Exponential, capped at 30 |
| Command concurrency | 1 per client | Different clients run in parallel |
| Shell | `powershell.exe -NoProfile -NonInteractive` | |
| Client ID location | `%LOCALAPPDATA%\RemoteCmd\client.id` | Persisted GUID |
| MCP transport | STDIO | |
| Server port | 7890 (TCP) | Hardcoded |
| Self-contained client exe | ~68 MB | Includes .NET runtime |

## 8. Tips & Gotchas

- **List before you execute.** On any session where there might be more than one target, call `remote_list_clients` first and confirm which machine you mean.

- **Name your clients explicitly.** Use `--name comos-1` when starting the client so the server has a memorable identifier. Without it, the default is `Environment.MachineName` — fine for unique hostnames, confusing when you have two `DESKTOP-XXXXX` machines.

- **One command at a time per client.** Different clients run in parallel but each client still serializes its commands. If you send a second command to the same client while the first is running you get `[ERROR] Another command is pending on '<name>'`.

- **Long-running commands need increased timeout.** Default is 30 s; set `timeoutSeconds` up to 300. But the client still kills at 60 s — for > 60 s work, use `Start-Job`.

- **File paths use Windows backslashes.** `remotePath` on a Windows target: `C:\Users\user\file.txt`.

- **Large files are held in memory** on both the relay and the MCP server. Plan for 200 MB per in-flight transfer.

- **Client reconnects automatically** with exponential backoff. After a server restart, wait a few seconds and it's back.

- **PowerShell profile is not loaded.** `-NoProfile` means custom aliases and profile scripts are not available.

- **stderr is included in output** prefixed with `[STDERR]`.

- **The token is the only security boundary.** Anyone with the token has full PowerShell access on every connected target. Treat it with the same care as a root password.

- **Backward compatibility:** A client without a `clientId` parameter is accepted as a "legacy-<ip>" session. Always upgrade both client and server together.
