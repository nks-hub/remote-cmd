[![CI](https://github.com/nks-hub/remote-cmd/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/remote-cmd/actions/workflows/ci.yml)
[![Release](https://github.com/nks-hub/remote-cmd/actions/workflows/release.yml/badge.svg)](https://github.com/nks-hub/remote-cmd/actions/workflows/release.yml)
[![GitHub Stars](https://img.shields.io/github/stars/nks-hub/remote-cmd?style=flat)](https://github.com/nks-hub/remote-cmd)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

# RemoteCmd

Run commands and move files on remote machines through NAT and firewalls, from scripts or from AI
agents. A relay sits in the middle; machines poll it over outbound HTTP, so they need no open ports.
The relay speaks MCP itself, so an agent such as Claude Code connects with one URL.

```
 AI agent / curl / script                Relay (RemoteCmd.Server)                Machines (RemoteCmd.Client)
 ────────────────────────                ────────────────────────                ───────────────────────────
 MCP  POST /mcp  (OAuth) ───────┐        :7890  relay port, token required  ◄─── poll every 800 ms (outbound only)
 REST /api/exec, /api/upload ───┴──────► :7891  dashboard, this machine only       run PowerShell / bash, move files
```

## Quick start

### 1. Install the relay

Windows (elevated PowerShell):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.ps1))) -Role server
```

Linux / macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.sh | sudo sh -s -- server
```

This installs the relay as a service (Windows Service, systemd or launchd) and prints the dashboard
address. Open **http://localhost:7891** on that machine.

### 2. Add machines

On the dashboard press **Pair & manage → Create code**. You get a one-time code and ready-made
commands with it filled in; run the one for the machine's system:

```powershell
# Windows, elevated PowerShell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.ps1))) -Server 'https://relay:7890' -Code 'K7F2Q-9XDMA'
```

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.sh | sudo sh -s -- client --server 'https://relay:7890' --code 'K7F2Q-9XDMA'
```

The script downloads the client for the right system and CPU (x64 or arm64), trades the code for the
machine's own key, pins the relay's certificate and starts the client as a service. Running the
same script again without a code upgrades the client and keeps its settings.

The code can also be created on the relay's command line: `RemoteCmd.Server pair --name build-01`.

### 3. Connect an AI agent

```bash
claude mcp add --transport http remote-cmd http://localhost:7891/mcp      # agent on the relay's machine
claude mcp add --transport http remote-cmd https://relay:7890/mcp         # agent elsewhere
```

The agent opens a browser page with a six-digit code. The same request appears on the relay's
dashboard, where you approve it after checking that the codes match. Approval issues the agent its
own credential; revoking it on the dashboard cuts the agent off.

Tools: `remote_list_clients`, `remote_exec`, `remote_write_file`, `remote_read_file` (file contents
travel inline, up to 20 MB; use the REST API for larger files) and `remote_screenshot`, which returns
a picture of the machine's screen.

A screenshot needs a desktop to photograph. A client installed as a Windows service runs in session 0
and captures a blank image; the reply says so rather than leaving the agent with an unexplained black
rectangle. Run the client in the interactive session when screenshots matter. Linux clients need one
of `grim`, `scrot`, `gnome-screenshot`, `spectacle` or ImageMagick's `import`; macOS and Windows need
nothing extra.

### 4. Scripts and curl

Create a controller token on the dashboard (**Access → New controller token**) or with
`RemoteCmd.Server token add --name ci`, then:

```bash
H="X-Token: <controller token>"
curl -H "$H" https://relay:7890/api/clients
curl -H "$H" -H "Content-Type: application/json" "https://relay:7890/api/exec?client=build-01" -d '{"command":"hostname","timeoutSeconds":30}'
curl -H "$H" --data-binary @local.zip "https://relay:7890/api/upload?client=build-01&path=C:/dest/file.zip"
curl -H "$H" -o out.log "https://relay:7890/api/download?client=build-01&path=/var/log/app.log"
```

Add `-k` while the relay uses its self-signed certificate.

## The dashboard

Listens on `http://localhost:7891` and nowhere else.

- **Status**: machines with their alias, real host name, account, system and client version
  (flagged when a newer release exists), live activity, command output, transfer volume.
- **Pair & manage**: pairing codes with install commands, AI agents waiting for approval, every
  credential with when it was last used and a revoke button, the relay's public address, and the
  dashboard password.

With no password the dashboard is open to anyone who has a session on the relay's machine. Set one
on the page or with `RemoteCmd.Server set-password`.

## Security

| What | How |
|------|-----|
| Machines | Each paired machine gets its own 256-bit key. It may only poll for work and answer it — never send commands, never list other machines, and never pose as another machine: its identity comes from its key, not from what it claims. |
| Controllers | Separate tokens for scripts and agents. Agents get theirs only through OAuth (PKCE, dynamic client registration) approved on the dashboard. |
| Storage | The relay keeps hashes of tokens and the derived encryption keys, never the tokens. Settings files are readable only by the owner (0600, or SYSTEM + Administrators + owner on Windows). Tokens stay off command lines and out of service definitions. |
| Pairing | The one-time code (50 bits, 15 min, burnt after 5 wrong proofs) authenticates an ECDH exchange, so the issued key never crosses the network in a usable form, even over plain HTTP. |
| Transport | TLS with the relay's own certificate; paired machines pin it and refuse anything else. Every command, result and file is additionally AES-256-GCM encrypted with the machine's key. |
| Dashboard | Loopback only; refuses foreign `Host` headers (DNS rebinding); every change needs a custom header a cross-site page cannot send; optional password with an HttpOnly, SameSite=Strict session. |
| Brute force | Wrong tokens, codes and passwords lock the sender out for 5 minutes after 10 tries a minute. |

Tokens given on the relay's command line (`RemoteCmd.Server <token>…`) still work, with full rights,
for setups from before pairing. Move them into the settings file with
`RemoteCmd.Server token import <token> --name fleet` and start the relay without them.

## Command reference

### Relay

```
RemoteCmd.Server [--port 7890] [--admin-port 7891] [--config <file>] [--no-tls] [--dashboard]
RemoteCmd.Server install-service | uninstall-service
RemoteCmd.Server pair [--name <machine>] [--ttl <minutes>]
RemoteCmd.Server token add --name <label> [--role controller|client]
RemoteCmd.Server token import <token> --name <label> [--role legacy|client|controller]
RemoteCmd.Server token list | token revoke <id>
RemoteCmd.Server set-password [--clear]
```

Settings live in `%ProgramData%\RemoteCmd\server.json` (Linux/macOS `/etc/remotecmd/server.json`),
or in the user's profile when that is not writable.

### Client

```
RemoteCmd.Client pair <server> <code> [--name <alias>] [--install-service]
RemoteCmd.Client [--dashboard] [--keep-awake]                    # run with saved settings
RemoteCmd.Client install-service | uninstall-service [--service-name <name>]
RemoteCmd.Client <server> <token> [--name <alias>]                # without pairing, as before
```

`<server>` may be a bare host (https on port 7890), `host:port` or a full URL. Several clients can
share a machine with different `--service-name` values.

### Legacy Windows 7 / .NET Framework 4.8 client

`RemoteCmd.Client48` is a wire-compatible port for hosts without .NET 9. It works with tokens
(`RemoteCmd.Client48.exe <server> <token> --name <alias>`, `install-service` likewise), not pairing.

### Android client (rooted devices)

`android/` is a Kotlin app with a foreground service that runs commands as root via `su`. Configure
server, token and name in the app. Build with `cd android && ./gradlew assembleDebug`.

## API

Controller endpoints take a token in the `X-Token` header or as `Authorization: Bearer`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/clients` | Machines with state, host, user, system and version |
| `GET`  | `/api/status[?client=X]` | Aggregate status, or one machine's |
| `POST` | `/api/exec[?client=X]` | Run `{"command":"...","timeoutSeconds":30}` |
| `POST` | `/api/upload?path=<remote>[&client=X]` | Upload a file (binary body, max 200 MB) |
| `GET`  | `/api/download?path=<remote>[&client=X]` | Download a file |
| `POST` | `/api/screenshot[?client=X][&path=<remote>]` | Photograph the screen, returns PNG |
| `GET`  | `/api/events[?limit=N]` | Recent relay activity and counters |
| `POST` | `/mcp` | MCP, Streamable HTTP, OAuth bearer token |

Without `client`, a command goes to the only connected machine; with several connected, the error
lists them.

## Build

```bash
dotnet build RemoteCmd.sln
dotnet test RemoteCmd.sln

dotnet publish RemoteCmd.Client -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/client
```

Releases ship the relay and client for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and
osx-arm64.

## Environment variables

| Variable | Where | Description |
|----------|-------|-------------|
| `REMOTECMD_PORT` / `REMOTECMD_ADMIN_PORT` | Relay | Ports (default 7890 / relay port + 1) |
| `REMOTECMD_CONFIG` | Relay | Settings file |
| `REMOTECMD_NO_TLS` | Relay | `1` serves plain HTTP (payloads stay encrypted) |
| `REMOTECMD_TOKEN(S)` | Relay | Command-line style tokens, full rights, this run only |
| `REMOTECMD_NO_UPDATE_CHECK` | Relay | `1` skips the GitHub release check |

## Support

- Email: dev@nks-hub.cz
- Bug reports: [GitHub Issues](https://github.com/nks-hub/remote-cmd/issues)

## License

Private — NKS Development

---

<p align="center">
  Made by <a href="https://github.com/nks-hub">NKS Hub</a>
</p>
