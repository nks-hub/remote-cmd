[![GitHub Stars](https://img.shields.io/github/stars/nks-hub/remote-cmd?style=flat)](https://github.com/nks-hub/remote-cmd)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

# RemoteCmd v2.0.0

Remote command execution relay for AI agents. Execute PowerShell commands and transfer files on remote machines through NAT/firewalls via HTTP polling.

## Architecture

```
+---------------------+     +----------------------+     +---------------------+
|   MCP Client        |     |   Relay Server       |     |   Target Machine    |
|   (Claude Code)     |     |   (.NET 9.0)         |     |   (.NET 9.0)        |
|                     |     |                      |     |                     |
|  +---------------+  |     |  HTTP API :7890       |     |  +---------------+  |
|  | MCP Server    |--+-----+-> /api/exec          |     |  | Client        |  |
|  | (Node.js)     |  |     |   /api/upload        |<----+--| (polling)     |  |
|  | stdio         |  |     |   /api/download      |     |  |               |  |
|  +---------------+  |     |   /api/status        |     |  | PowerShell    |  |
|                     |     |                      |     |  | execution     |  |
+---------------------+     +----------------------+     +---------------------+
```

### Components

| Component | Runtime | Description |
|-----------|---------|-------------|
| **RemoteCmd.Server** | .NET 9.0 | HTTP relay server, accepts commands and proxies to client |
| **RemoteCmd.Client** | .NET 9.0 | Runs on target machine, polls server for commands, executes via PowerShell |
| **mcp-server** | Node.js | MCP (Model Context Protocol) bridge for Claude Code integration |

### How it works

1. **Client** on target machine polls **Server** every 800ms for pending commands/file transfers
2. **Controller** (Claude Code via MCP, or curl) sends command to **Server** HTTP API
3. **Server** queues command, waits for **Client** to pick it up
4. **Client** executes via PowerShell, sends result back to **Server**
5. **Server** returns result to **Controller**

Client connects outbound to the server - works through any firewall/NAT that allows HTTP.

## Quick Start

### 1. Start Relay Server

```bash
dotnet run --project RemoteCmd.Server -- <TOKEN>

# Example:
dotnet run --project RemoteCmd.Server -- mySecretToken

# With custom bind address:
dotnet run --project RemoteCmd.Server -- mySecretToken --bind http://0.0.0.0:7890

# Show token hash on startup (for verification):
dotnet run --project RemoteCmd.Server -- mySecretToken --show-token
```

Server listens on `http://0.0.0.0:7890` by default. Token is required for all API endpoints via `Authorization: Bearer <TOKEN>` header.

### 2. Start Client on Target Machine

```bash
# From source
dotnet run --project RemoteCmd.Client -- <SERVER_IP> <TOKEN>

# Or publish self-contained exe (no .NET runtime needed on target)
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/client

# Then copy and run on target:
RemoteCmd.Client.exe <SERVER_IP> <TOKEN>

# With certificate pinning (recommended for HTTPS):
RemoteCmd.Client.exe <SERVER_IP> <TOKEN> --cert-pin <SHA256_THUMBPRINT>
```

### 3. Configure MCP for Claude Code

Add to your `.mcp.json` or Claude Code MCP settings:

```json
{
  "mcpServers": {
    "remote-cmd": {
      "type": "stdio",
      "command": "node",
      "args": ["<path-to>/mcp-server/index.mjs"],
      "env": {
        "REMOTECMD_URL": "https://localhost:7890",
        "REMOTECMD_TOKEN": "<TOKEN>"
      }
    }
  }
}
```

### 4. Use via curl (without MCP)

```bash
# Execute command
curl -X POST "http://localhost:7890/api/exec" \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"command":"hostname","timeoutSeconds":30}'

# Upload file to remote
curl -X POST "http://localhost:7890/api/upload?path=C:\dest\file.zip" \
  -H "Authorization: Bearer <TOKEN>" \
  --data-binary @local.zip

# Download file from remote
curl -o local.zip "http://localhost:7890/api/download?path=C:\remote\file.zip" \
  -H "Authorization: Bearer <TOKEN>"

# Check client status
curl "http://localhost:7890/api/status" \
  -H "Authorization: Bearer <TOKEN>"
```

## Environment Variables

### MCP Server (Node.js)

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `REMOTECMD_TOKEN` | Yes | - | Authentication token (Bearer) |
| `REMOTECMD_URL` | No | `https://localhost:7890` | Relay server URL |
| `REMOTECMD_CA_CERT` | No | - | Path to custom CA certificate file (.pem/.crt) for HTTPS verification |

When `REMOTECMD_CA_CERT` is set, the MCP server validates the relay server's TLS certificate against that CA. Without it, the server accepts self-signed certificates (scoped to the relay agent only - does not affect global Node.js TLS).

### rcmd.sh

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `REMOTECMD_TOKEN` | Yes | - | Authentication token |
| `REMOTECMD_URL` | No | `http://localhost:7890` | Relay server URL |

## Command Policy (commandpolicy.json)

The client enforces a command policy loaded from `commandpolicy.json` in the working directory. Copy the example to get started:

```bash
cp RemoteCmd.Client/commandpolicy.json.example commandpolicy.json
```

**Example configuration:**

```json
{
    "mode": "denylist",
    "allowedPatterns": [],
    "deniedPatterns": [
        "Invoke-WebRequest", "Invoke-RestMethod", "Start-Process",
        "-EncodedCommand", "net user", "Add-LocalGroupMember",
        "Set-ExecutionPolicy", "New-Service"
    ],
    "allowedPaths": [],
    "maxCommandLength": 4096
}
```

| Field | Description |
|-------|-------------|
| `mode` | `denylist` (block specific patterns) or `allowlist` (allow only specific patterns) |
| `allowedPatterns` | Regex patterns allowed in allowlist mode |
| `deniedPatterns` | Regex patterns always blocked |
| `allowedPaths` | File path prefixes permitted for upload/download |
| `maxCommandLength` | Maximum command length in characters |

`commandpolicy.json` is gitignored - each deployment configures its own policy.

## MCP Tools

When connected via MCP, Claude Code gets these tools:

| Tool | Description |
|------|-------------|
| `remote_exec` | Execute PowerShell command on remote machine |
| `remote_status` | Check if client is connected |
| `remote_upload` | Upload file from local to remote (max 200MB) |
| `remote_download` | Download file from remote to local (max 200MB) |

## API Reference

All endpoints require `Authorization: Bearer <TOKEN>` header.

> Note: `?token=` query parameter is no longer supported (removed in v2).

### Public Endpoints

| Method | Endpoint | Description | Body |
|--------|----------|-------------|------|
| `GET` | `/api/status` | Check client connection | - |
| `POST` | `/api/exec` | Execute command | `{"command":"...","timeoutSeconds":30}` |
| `POST` | `/api/upload?path=<remote>` | Upload file to remote | Binary file data |
| `GET` | `/api/download?path=<remote>` | Download file from remote | - |

### Command Execution

**Request:**
```json
{
  "command": "Get-Process | Select-Object -First 5",
  "timeoutSeconds": 30
}
```

**Response:**
```json
{
  "output": "Handles  NPM(K)  PM(K)  WS(K)  CPU(s)    Id  SI ProcessName\n...",
  "exitCode": 0
}
```

### File Upload

**Request:** `POST /api/upload?path=C:\Users\user\file.dll`
- Header: `Authorization: Bearer <TOKEN>`
- Body: raw binary file data
- Content-Type: `application/octet-stream`

**Response:**
```json
{
  "status": "ok",
  "size": 254976
}
```

### File Download

**Request:** `GET /api/download?path=C:\Users\user\file.log`
- Header: `Authorization: Bearer <TOKEN>`

**Response:** Binary file data with `Content-Disposition` header.

### Status

**Response:**
```json
{
  "clientConnected": true,
  "lastPoll": "2026-02-11T14:20:18Z",
  "secondsAgo": 2
}
```

### Internal Endpoints (used by Client)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/poll` | Client polls for pending commands |
| `POST` | `/api/result` | Client posts command result |
| `GET` | `/api/file-poll` | Client polls for pending file transfers |
| `GET` | `/api/file-data` | Client downloads file data (upload-to-remote) |
| `POST` | `/api/file-done` | Client confirms file saved |
| `POST` | `/api/file-upload` | Client uploads file data (download-from-remote) |

## Build

```bash
# Build both projects
dotnet build RemoteCmd.sln

# Publish self-contained client (no .NET runtime needed)
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/client

# Install MCP server dependencies
cd mcp-server && npm install
```

## Network Setup

### Requirements

- Server must be reachable from client on port 7890 (TCP)
- Client initiates all connections (outbound HTTP) - no inbound ports needed on client

### Firewall Rules

```powershell
# Windows Firewall - allow inbound on server
netsh advfirewall firewall add rule name="RemoteCmd" dir=in action=allow protocol=tcp localport=7890
```

### NAT Port Forward (MikroTik)

```
/ip firewall nat add chain=dstnat dst-port=7890 protocol=tcp \
  action=dst-nat to-addresses=<SERVER_LAN_IP> to-ports=7890 \
  comment="RemoteCmd relay"
```

## Security

### Encryption Layers

| Layer | Technology | Scope |
|-------|-----------|-------|
| **Transport** | TLS 1.2+ (self-signed certificate) | Server <-> Client HTTPS |
| **Payload** | AES-256-GCM | All commands, results, file data, metadata |
| **Authentication** | Bearer token (Authorization header) | All API endpoints |

### How it works

1. **TLS**: Server auto-generates a self-signed X.509 certificate (RSA 2048, SHA256, valid 5 years). MCP server uses a per-request TLS agent scoped to the relay URL only.
2. **AES-256-GCM**: Encryption key is derived from the shared token via `SHA256("RemoteCmd:v1:" + token)`. Every payload uses a random 12-byte nonce. GCM provides both confidentiality and integrity (16-byte auth tag).
3. **Certificate pinning**: Client supports `--cert-pin <SHA256>` to pin the server certificate thumbprint.
4. **What's encrypted**: Commands, command results, file transfer metadata (paths, sizes), file data. Status and auth endpoints use plaintext (no sensitive data).

### Custom CA Certificate (MCP Server)

For production deployments with a proper CA:

```json
{
  "env": {
    "REMOTECMD_URL": "https://myrelay.example.com:7890",
    "REMOTECMD_TOKEN": "<TOKEN>",
    "REMOTECMD_CA_CERT": "/path/to/ca.crt"
  }
}
```

### Disabling TLS

Use `--no-tls` flag on server for HTTP-only mode (AES payload encryption still active):

```bash
dotnet run --project RemoteCmd.Server -- myToken --no-tls
```

Client connects via HTTP when server URL starts with `http://`:

```bash
RemoteCmd.Client.exe http://192.168.1.100:7890 myToken
```

## Technical Details

| Parameter | Value |
|-----------|-------|
| Client poll interval | 800ms |
| Command timeout | Configurable per request (default 30s, max 300s) |
| Process kill timeout | 60s |
| File transfer timeout | 5 minutes |
| Max file size | 200MB |
| Auto-reconnect | Exponential backoff (1s to 30s) |
| Concurrency | Single command at a time (SemaphoreSlim) |
| Shell | `powershell.exe -NoProfile -NonInteractive` |
| Transport encryption | TLS 1.2+ (self-signed, auto-generated) |
| Payload encryption | AES-256-GCM (key derived from token) |
| Authentication | Bearer token (Authorization header) |
| Client detection | Connected if last poll < 10 seconds ago |

## Shell Helper

```bash
# Set token via environment variable
export REMOTECMD_TOKEN=mySecretToken
export REMOTECMD_URL=http://localhost:7890   # optional

./rcmd.sh "hostname"
./rcmd.sh "Get-Process" 60   # with 60s timeout
```

## Migration Guide: v1 to v2

### Breaking changes

| Area | v1 | v2 |
|------|----|----|
| Authentication | `?token=` query parameter | `Authorization: Bearer <TOKEN>` header |
| TLS (MCP server) | `NODE_TLS_REJECT_UNAUTHORIZED=0` (global) | Per-request agent (scoped) |
| Token in rcmd.sh | Hardcoded in script | `REMOTECMD_TOKEN` env var |

### Steps

1. **Update curl commands** - replace `?token=<TOKEN>` with `-H "Authorization: Bearer <TOKEN>"`
2. **Update rcmd.sh** - set `REMOTECMD_TOKEN` env var instead of editing the script
3. **MCP config** - no change needed, `REMOTECMD_TOKEN` env var was already used
4. **Server** - `Authorization: Bearer` header is now required; `?token=` query param no longer accepted

## Project Structure

```
RemoteCmd.sln
+-- RemoteCmd.Server/        # HTTPS relay server (.NET 9.0)
|   +-- Program.cs
|   +-- Crypto.cs            # AES-256-GCM encryption
+-- RemoteCmd.Client/        # Target machine client (.NET 9.0)
|   +-- Program.cs
|   +-- Crypto.cs            # AES-256-GCM encryption
|   +-- CommandPolicy.cs     # Command allow/denylist enforcement
|   +-- PathValidator.cs     # Path validation helpers
|   +-- commandpolicy.json.example
+-- RemoteCmd.Shared/        # Shared types
+-- mcp-server/              # MCP bridge (Node.js)
|   +-- index.mjs
|   +-- package.json
|   +-- package-lock.json
+-- rcmd.sh                  # Shell helper script
```

## Contributing

Contributions are welcome! For major changes, please open an issue first.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: description'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## Support

- **Email:** dev@nks-hub.cz
- **Bug reports:** [GitHub Issues](https://github.com/nks-hub/remote-cmd/issues)

## License

Private - NKS Development
