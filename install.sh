#!/bin/sh
# Installs the RemoteCmd client or relay on Linux or macOS, registers it as a service and, for a
# client, pairs it with a relay.
#
#   Client:  curl -fsSL https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.sh | sudo sh -s -- client --server https://relay:7890 --code K7F2Q-9XDMA
#   Relay:   curl -fsSL https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.sh | sudo sh -s -- server
#   Upgrade: run it again without --code; the saved settings are kept.
set -eu

role=client
server=""
code=""
name=""
version=latest
dir=""
no_service=0
service_name=""

case "${1:-}" in
  client|server) role=$1; shift ;;
esac
while [ $# -gt 0 ]; do
  case "$1" in
    --server) server=$2; shift 2 ;;
    --code) code=$2; shift 2 ;;
    --name) name=$2; shift 2 ;;
    --version) version=$2; shift 2 ;;
    --dir) dir=$2; shift 2 ;;
    --service-name) service_name=$2; shift 2 ;;
    --no-service) no_service=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) echo "Unsupported system: $(uname -s)" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac

if [ "$role" = server ]; then project=RemoteCmd.Server; service=RemoteCmdServer; else project=RemoteCmd.Client; service=RemoteCmdClient; fi
asset="$project-$os-$arch.tar.gz"
releases=https://github.com/nks-hub/remote-cmd/releases
if [ "$version" = latest ]; then url="$releases/latest/download/$asset"; else url="$releases/download/v${version#v}/$asset"; fi
# A second client on one machine needs its own service, settings and folder.
if [ -n "$service_name" ]; then service=$service_name; fi
[ -n "$dir" ] || dir="/opt/remotecmd/$role${service_name:+-$service_name}"

if [ "$no_service" = 0 ] && [ "$(id -u)" != 0 ]; then
  echo "Installing a service needs root: pipe into 'sudo sh', or add --no-service." >&2
  exit 1
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "Downloading $url"
if command -v curl >/dev/null 2>&1; then curl -fsSL "$url" -o "$tmp/$asset"; else wget -q "$url" -O "$tmp/$asset"; fi

# A running service keeps the old binary busy; stop it before replacing it.
if [ "$os" = linux ]; then
  systemctl stop "$service" 2>/dev/null || true
else
  launchctl bootout "system/cz.nks.remotecmd.$service" 2>/dev/null || true
fi

mkdir -p "$dir"
tar -xzf "$tmp/$asset" -C "$dir"
chmod +x "$dir/$project"
# Downloaded with curl there is no quarantine flag, but a copy fetched by a browser would have one.
[ "$os" = osx ] && xattr -dr com.apple.quarantine "$dir" 2>/dev/null || true
echo "Installed $("$dir/$project" --version) to $dir"

if [ "$role" = client ]; then
  if [ -n "$code" ]; then
    [ -n "$server" ] || { echo "--code needs --server too." >&2; exit 2; }
    set -- pair "$server" "$code"
    [ -z "$service_name" ] || set -- "$@" --service-name "$service_name"
    [ -z "$name" ] || set -- "$@" --name "$name"
    [ "$no_service" = 1 ] || set -- "$@" --install-service
    "$dir/$project" "$@"
  elif [ "$no_service" = 0 ]; then
    # No code: an upgrade of a machine that is already paired.
    "$dir/$project" install-service ${service_name:+--service-name "$service_name"} || { echo "No saved settings. Pass --server and --code from the relay dashboard." >&2; exit 1; }
  fi
else
  [ "$no_service" = 1 ] || "$dir/$project" install-service ${service_name:+--service-name "$service_name"}
  echo "Open the dashboard on this machine: http://localhost:7891  (press \"Pair a machine\")"
fi
