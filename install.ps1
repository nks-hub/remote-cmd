<#
.SYNOPSIS
  Installs the RemoteCmd client or relay on Windows, registers it as a service and, for a client,
  pairs it with a relay.

.EXAMPLE
  # Client, paired with the code from the relay's dashboard (elevated PowerShell):
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.ps1))) -Server 'https://relay:7890' -Code 'K7F2Q-9XDMA'

.EXAMPLE
  # Relay:
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.ps1))) -Role server

.EXAMPLE
  # Upgrade whatever is installed, keeping its settings: run again without -Code.
#>
param(
    [ValidateSet('client', 'server')] [string] $Role = 'client',
    [string] $Server,
    [string] $Code,
    [string] $Name,
    [string] $Version = 'latest',
    [string] $InstallDir,
    [string] $ServiceName,
    [switch] $NoService
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest crawl on PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $NoService -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator), or add -NoService.'
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$project = if ($Role -eq 'server') { 'RemoteCmd.Server' } else { 'RemoteCmd.Client' }
$service = if ($ServiceName) { $ServiceName } elseif ($Role -eq 'server') { 'RemoteCmdServer' } else { 'RemoteCmdClient' }
# A second client on one machine needs its own service, settings and folder.
$extra = if ($ServiceName) { @('--service-name', $ServiceName) } else { @() }
$asset = "$project-win-$arch.zip"
$releases = 'https://github.com/nks-hub/remote-cmd/releases'
$url = if ($Version -eq 'latest') { "$releases/latest/download/$asset" } else { "$releases/download/v$($Version.TrimStart('v'))/$asset" }
if (-not $InstallDir) { $InstallDir = Join-Path $env:ProgramFiles ("RemoteCmd\$Role" + $(if ($ServiceName) { "-$ServiceName" } else { '' })) }

$tmp = Join-Path $env:TEMP ("remotecmd-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile (Join-Path $tmp $asset) -UseBasicParsing

    # A running service holds its executable open; stop it so the new one can be put in place.
    if (Get-Service -Name $service -ErrorAction SilentlyContinue) {
        Stop-Service -Name $service -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Expand-Archive -Path (Join-Path $tmp $asset) -DestinationPath $InstallDir -Force
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$exe = Join-Path $InstallDir "$project.exe"
Write-Host "Installed $(& $exe --version) to $InstallDir"

if ($Role -eq 'client') {
    if ($Code) {
        if (-not $Server) { throw '-Code needs -Server too.' }
        $pairArgs = @('pair', $Server, $Code) + $extra
        if ($Name) { $pairArgs += @('--name', $Name) }
        if (-not $NoService) { $pairArgs += '--install-service' }
        & $exe @pairArgs
        if ($LASTEXITCODE -ne 0) { throw "Pairing failed (exit $LASTEXITCODE)." }
    }
    elseif (-not $NoService) {
        # No code: an upgrade of a machine that is already paired.
        & $exe install-service @extra
        if ($LASTEXITCODE -ne 0) { throw 'No saved settings to start from. Pass -Server and -Code from the relay dashboard.' }
    }
}
else {
    if (-not $NoService) {
        & $exe install-service @extra
        if ($LASTEXITCODE -ne 0) { throw "Service install failed (exit $LASTEXITCODE)." }
    }
    Write-Host 'Open the dashboard on this machine: http://localhost:7891  (Pair & manage -> Create code)'
}
