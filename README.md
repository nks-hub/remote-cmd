# RemoteCmd v1.0.0

Remote command execution relay for AI agents. Execute PowerShell commands and transfer files on remote machines through NAT/firewalls via HTTP polling.

## Architecture

```
┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────────┐
│   MCP Client        │     │   Relay Server       │     │   Target Machine    │
│   (Claude Code)     │     │   (.NET 9.0)         │     │   (.NET 9.0)        │
│                     │     │                      │     │                     │
│  ┌───────────────┐  │     │  HTTP API :7890       │     │  ┌───────────────┐  │
│  │ MCP Server    │──┼─────┼─> /api/exec          │     │  │ Client        │  │
│  │ (Node.js)     │  │     │   /api/upload        │◄────┼──│ (polling)     │  │
│  │ stdio         │  │     │   /api/download      │     │  │               │  │
│  └───────────────┘  │     │   /api/status        │     │  │ PowerShell    │  │
│                     │     │                      │     │  │ execution     │  │
└─────────────────────┘     └──────────────────────┘     └─────────────────────┘
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
```

Server listens on `http://0.0.0.0:7890`. Token is used for authentication on all API endpoints.

### 2. Start Client on Target Machine

```bash
# From source
dotnet run --project RemoteCmd.Client -- <SERVER_IP> <TOKEN>

# Or publish self-contained exe (no .NET runtime needed on target)
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/client

# Then copy and run on target:
RemoteCmd.Client.exe <SERVER_IP> <TOKEN>
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
        "REMOTECMD_URL": "http://localhost:7890",
        "REMOTECMD_TOKEN": "<TOKEN>"
      }
    }
  }
}
```

### 4. Use via curl (without MCP)

```bash
# Execute command
curl -X POST "http://localhost:7890/api/exec?token=<TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"command":"hostname","timeoutSeconds":30}'

# Upload file to remote
curl -X POST "http://localhost:7890/api/upload?token=<TOKEN>&path=C:\dest\file.zip" \
  --data-binary @local.zip

# Download file from remote
curl -o local.zip "http://localhost:7890/api/download?token=<TOKEN>&path=C:\remote\file.zip"

# Check client status
curl "http://localhost:7890/api/status?token=<TOKEN>"
```

## MCP Tools

When connected via MCP, Claude Code gets these tools:

| Tool | Description |
|------|-------------|
| `remote_exec` | Execute PowerShell command on remote machine |
| `remote_status` | Check if client is connected |
| `remote_upload` | Upload file from local to remote (max 200MB) |
| `remote_download` | Download file from remote to local (max 200MB) |

## API Reference

All endpoints require `?token=<TOKEN>` query parameter or `X-Token` header.

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

**Request:** `POST /api/upload?token=xxx&path=C:\Users\user\file.dll`
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

**Request:** `GET /api/download?token=xxx&path=C:\Users\user\file.log`

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
| Authentication | Token-based (query param or header) |
| Client detection | Connected if last poll < 10 seconds ago |

## Shell Helper

```bash
# rcmd.sh - quick command execution
./rcmd.sh "hostname"
./rcmd.sh "Get-Process" 60   # with 60s timeout
```

## Project Structure

```
RemoteCmd.sln
├── RemoteCmd.Server/        # HTTP relay server (.NET 9.0)
│   └── Program.cs
├── RemoteCmd.Client/        # Target machine client (.NET 9.0)
│   └── Program.cs
├── mcp-server/              # MCP bridge (Node.js)
│   ├── index.mjs
│   └── package.json
└── rcmd.sh                  # Shell helper script
```

## License

Private - NKS Development
