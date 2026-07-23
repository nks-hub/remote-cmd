[![CI](https://github.com/nks-hub/remote-cmd/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/remote-cmd/actions/workflows/ci.yml)
[![Release](https://github.com/nks-hub/remote-cmd/actions/workflows/release.yml/badge.svg)](https://github.com/nks-hub/remote-cmd/actions/workflows/release.yml)
[![GitHub Stars](https://img.shields.io/github/stars/nks-hub/remote-cmd?style=flat)](https://github.com/nks-hub/remote-cmd)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

# RemoteCmd v1.2.0

Remote command execution relay for AI agents. Execute PowerShell commands and transfer files on remote machines through NAT/firewalls via HTTP polling. **Multi-client support** — a single relay can serve many target machines and route commands to a specific one by name.

## Architecture

```
+---------------------+     +----------------------+     +---------------------+
|   MCP Client        |     |   Relay Server       |     |   Target Machines   |
|   (Claude Code)     |     |   (.NET 9.0)         |     |   (.NET 9.0)        |
|                     |     |                      |     |                     |
|  +---------------+  |     |  HTTP API :7890      |     |  +---------------+  |
|  | MCP Server    |--+-----+-> /api/exec          |     |  | Client A      |  |
|  | (Node.js)     |  |     |  /api/upload         |<----+--| (polling)     |  |
|  | stdio         |  |     |  /api/download       |     |  +---------------+  |
|  +---------------+  |     |  /api/clients        |<----+--| Client B ...  |  |
|                     |     |                      |     |  +---------------+  |
+---------------------+     +----------------------+     +---------------------+
```

### Components

| Component | Runtime | Description |
|-----------|---------|-------------|
| **RemoteCmd.Server** | .NET 9.0 | HTTP relay server, accepts commands and proxies to the targeted client |
| **RemoteCmd.Client** | .NET 9.0 | Runs on each target machine, polls server, executes via PowerShell |
| **RemoteCmd.Shared** | .NET 9.0 | Shared `Crypto` (AES-256-GCM) library |
| **mcp-server** | Node.js | MCP (Model Context Protocol) bridge for Claude Code integration |

### How it works

1. Each **Client** on a target machine registers with a stable `clientId` (persisted to disk) and a human-readable `name` (default: `Environment.MachineName`, override via `--name`).
2. Client polls **Server** every 800 ms for pending commands and file transfers scoped to its session.
3. **Controller** (Claude Code via MCP, or curl) sends a command to the **Server**, optionally with `?client=<name|id>` to target a specific machine.
4. The **Server** queues the command on that client's session, waits for the result, returns it.

Clients only need outbound HTTP. No inbound ports on the target machines.

## Quick Start

### 1. Start the Relay Server

```bash
dotnet run --project RemoteCmd.Server -- <TOKEN>

# With env vars (useful for systemd, containers, tests):
REMOTECMD_TOKEN=<TOKEN> REMOTECMD_NO_TLS=1 dotnet run --project RemoteCmd.Server
```

Server listens on `http://0.0.0.0:7890` (or `https://` with TLS). Token is used for authentication on all API endpoints.

### 2. Start Clients on Target Machines

```bash
# From source
dotnet run --project RemoteCmd.Client -- <SERVER_IP> <TOKEN> [--name <alias>]

# Or the published self-contained exe:
RemoteCmd.Client.exe <SERVER_IP> <TOKEN> --name comos-1
```

#### One-line PowerShell bootstrap (Windows)

Downloads the latest self-contained client from GitHub Releases, generates a random
key, and connects. Edit `$Server` to point at your relay; the printed key is the
shared secret the relay operator must start the server with (or add for this client).

```powershell
$Server = 'http://YOUR_RELAY_HOST:7890'   # <-- change to your relay URL
$Token  = -join ((48..57 + 65..90 + 97..122) | Get-Random -Count 24 | % { [char]$_ })
$Dir = "$env:TEMP\remotecmd"; New-Item $Dir -ItemType Directory -Force | Out-Null
Invoke-WebRequest 'https://github.com/nks-hub/remote-cmd/releases/latest/download/RemoteCmd.Client-win-x64.zip' -OutFile "$Dir\client.zip"
Expand-Archive "$Dir\client.zip" $Dir -Force
Write-Host "Client key (give this to the relay operator): $Token"
& "$Dir\RemoteCmd.Client.exe" $Server $Token --name $env:COMPUTERNAME
```

Condensed to a single line for copy-paste:

```powershell
$S='http://YOUR_RELAY_HOST:7890';$T=-join((48..57+65..90+97..122)|Get-Random -Count 24|%{[char]$_});$D="$env:TEMP\remotecmd";ni $D -ItemType Directory -Force|Out-Null;iwr 'https://github.com/nks-hub/remote-cmd/releases/latest/download/RemoteCmd.Client-win-x64.zip' -OutFile "$D\c.zip";Expand-Archive "$D\c.zip" $D -Force;Write-Host "KEY: $T";& "$D\RemoteCmd.Client.exe" $S $T --name $env:COMPUTERNAME
```

Note: for an HTTP (`--no-tls`) relay the URL **must** start with `http://` and include
the port, otherwise the client assumes `https://<host>` on port 443.

Each client persists its GUID to `%LOCALAPPDATA%\RemoteCmd\client.<name>.id` (Linux/macOS: `$XDG_DATA_HOME/RemoteCmd/` or `~/.local/share/RemoteCmd/`). The ID survives restarts. The id file is **scoped per `--name`**, so multiple aliased instances on the same machine (e.g. elevated + non-elevated) get distinct ids and don't compete for the same session. A legacy `client.id` is auto-migrated for the default machine-name instance.

#### Run as a system service

The .NET 9 client can register itself as a **Windows Service** or **systemd unit** so it
starts at boot and restarts on failure (needs admin / root):

```bash
# Install (Windows Service on Windows, systemd unit on Linux)
RemoteCmd.Client install-service <SERVER_IP> <TOKEN> --name comos-1 [--service-name <name>]

# Remove
RemoteCmd.Client uninstall-service [--service-name <name>]
```

Default service name is `RemoteCmdClient`. The service runs the same poll loop as the
console mode (`--service` is the host marker used internally by the SCM / systemd).

#### Legacy Windows 7 / .NET Framework 4.8 client

`RemoteCmd.Client48` is a wire-compatible port for hosts without .NET 9 (e.g. Windows 7).
It ships as `RemoteCmd.Client48.exe` plus `BouncyCastle.Cryptography.dll` (AES-256-GCM via
BouncyCastle). Same command surface, Windows-only service registration:

```bat
RemoteCmd.Client48.exe <SERVER_IP> <TOKEN> --name bmw-vm
RemoteCmd.Client48.exe install-service <SERVER_IP> <TOKEN> --name bmw-vm
```

#### Android client (rooted devices)

`android/` is a Kotlin app (`cz.nks.remotecmd`) with full parity: AES-256-GCM, command
exec, and 200MB file transfer. It runs a **foreground service**, auto-starts on boot, and
executes commands as **root via `su`** (Magisk `su -c` and AOSP `su 0` are both detected).
Configure server/token/name in the UI and tap **Start**. Build a debug APK with
`cd android && ./gradlew assembleDebug` (output: `app/build/outputs/apk/debug/app-debug.apk`).
From the host the relay is reachable at `http://10.0.2.2:7890` on an emulator.

### 3. Configure MCP for Claude Code

```json
{
  "mcpServers": {
    "remote-cmd": {
      "type": "stdio",
      "command": "node",
      "args": ["<path-to>/mcp-server/index.mjs"],
      "env": {
        "REMOTECMD_URL": "https://localhost:7890",
        "REMOTECMD_TOKEN": "<TOKEN>",
        "REMOTECMD_DEFAULT_CLIENT": "comos-1"
      }
    }
  }
}
```

`REMOTECMD_DEFAULT_CLIENT` is optional — when set, tools default to that client unless overridden via the `client` argument.

### 4. Use via curl

```bash
# List all clients
curl "http://localhost:7890/api/clients?token=<TOKEN>"

# Execute command on the single connected client (auto-select)
curl -X POST "http://localhost:7890/api/exec?token=<TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"command":"hostname","timeoutSeconds":30}'

# Target a specific client by name
curl -X POST "http://localhost:7890/api/exec?token=<TOKEN>&client=comos-1" \
  -H "Content-Type: application/json" \
  -d '{"command":"hostname"}'

# Upload file to a specific client
curl -X POST "http://localhost:7890/api/upload?token=<TOKEN>&client=comos-1&path=C:\dest\file.zip" \
  --data-binary @local.zip
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `remote_list_clients` | List all known clients with connection status |
| `remote_status` | Check aggregate or single-client status |
| `remote_exec` | Execute PowerShell on a target client |
| `remote_upload` | Upload file from local to remote client (max 200MB) |
| `remote_download` | Download file from remote client to local (max 200MB) |

Every tool except `remote_list_clients` accepts an optional `client` argument (name or id). When omitted, the server auto-selects if exactly one client is connected; otherwise an error with the list of connected clients is returned.

## API Reference

All endpoints require a token — via `?token=<TOKEN>`, `X-Token: <TOKEN>` header, or `Authorization: Bearer <TOKEN>`.

### Controller endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/clients` | List all clients (`{count, connected, clients: [...]}`) |
| `GET`  | `/api/status[?client=X]` | Aggregate status; with `client` returns per-client details |
| `POST` | `/api/exec[?client=X]` | Execute command `{"command":"...","timeoutSeconds":30}` |
| `POST` | `/api/upload?path=<remote>[&client=X]` | Upload file (binary body) |
| `GET`  | `/api/download?path=<remote>[&client=X]` | Download file |

### Client-facing polling endpoints

Clients identify themselves via `?clientId=<guid>&name=<hostname>` on every polling request.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/poll` | Poll for pending command (encrypted) |
| `POST` | `/api/result` | Post encrypted command result |
| `GET`  | `/api/file-poll` | Poll for pending file transfer |
| `GET`  | `/api/file-data` | Download file data for upload-to-remote |
| `POST` | `/api/file-done` | Confirm file saved |
| `POST` | `/api/file-upload` | Upload file data for download-from-remote |

### Target resolution rules

1. If `?client=<name|id>` is specified → that session (404 if unknown, 400 if not connected).
2. Else if exactly one client is connected → that one.
3. Else → error listing connected client names.

## Build

```bash
# Build + test
dotnet build RemoteCmd.sln
dotnet test RemoteCmd.sln

# MCP tests
cd mcp-server && npm install && npm test

# Publish self-contained client
dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/client
```

## Environment Variables

| Variable | Where | Default | Description |
|----------|-------|---------|-------------|
| `REMOTECMD_TOKEN` | Server, MCP | — | Shared authentication token |
| `REMOTECMD_NO_TLS` | Server | unset | `1`/`true` disables TLS (fallback when `--no-tls` is not passed) |
| `REMOTECMD_PORT` | Server | `7890` | Listen port (CI / running multiple instances) |
| `REMOTECMD_URL` | MCP | `https://localhost:7890` | Relay URL |
| `REMOTECMD_DEFAULT_CLIENT` | MCP | unset | Name or id of client to target when `client` arg is omitted |

## Security

| Layer | Technology | Scope |
|-------|-----------|-------|
| Transport | TLS 1.2+ (self-signed cert) | Server ↔ Client HTTPS |
| Payload | AES-256-GCM | All commands, results, file data, metadata |
| Authentication | Shared token, constant-time comparison | All `/api/*` endpoints |

Token may be passed via `?token=`, `X-Token:` header, or `Authorization: Bearer`. Prefer the header or Bearer form in production — query strings leak into proxy logs.

Use `--no-tls` (or `REMOTECMD_NO_TLS=1`) on the server for HTTP-only mode (AES payload encryption stays active).

## Technical Details

| Parameter | Value |
|-----------|-------|
| Client poll interval | 800 ms |
| Command timeout | Configurable per request (default 30 s, max 300 s) |
| Process kill timeout | 60 s (client-side) |
| File transfer timeout | 5 minutes |
| Max file size / body | 200 MB |
| Auto-reconnect | Exponential backoff (1 s → 30 s) |
| Concurrency | Per-client `SemaphoreSlim(1)` — each machine serial, multiple machines in parallel |
| Shell | `powershell.exe -NoProfile -NonInteractive` |
| Client detection | Connected if last poll < 10 s ago |

## Project Structure

```
RemoteCmd.sln
├── RemoteCmd.Shared/       # Shared Crypto (AES-256-GCM)
├── RemoteCmd.Server/       # HTTPS relay server
├── RemoteCmd.Client/       # Target machine client (.NET 9, console / service)
├── RemoteCmd.Client48/     # .NET Framework 4.8 client (Windows 7+, BouncyCastle)
├── RemoteCmd.Tests/        # xUnit unit + integration tests
├── android/                # Rooted Android client (Kotlin, foreground service)
├── mcp-server/             # MCP bridge (Node.js)
│   └── tests/              # node --test validation tests
├── .github/workflows/      # CI + Release
│   ├── ci.yml
│   └── release.yml
└── rcmd.sh                 # Shell helper
```

## Contributing

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: description'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## Support

- Email: dev@nks-hub.cz
- Bug reports: [GitHub Issues](https://github.com/nks-hub/remote-cmd/issues)

## License

Private — NKS Development

---

<p align="center">
  Made by <a href="https://github.com/nks-hub">NKS Hub</a>
</p>
