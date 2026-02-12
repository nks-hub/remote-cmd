#!/bin/bash
# RemoteCmd helper - usage: rcmd.sh "powershell command" [timeout]
TOKEN="heslo123"
URL="http://localhost:7890"
TIMEOUT="${2:-30}"

# Write command to temp json file to avoid escaping issues
TMPFILE=$(mktemp /tmp/rcmd.XXXXXX.json)
python3 -c "import json,sys; json.dump({'command':sys.argv[1],'timeoutSeconds':int(sys.argv[2])},open(sys.argv[3],'w'))" "$1" "$TIMEOUT" "$TMPFILE"
curl -s -X POST "$URL/api/exec?token=$TOKEN" -H "Content-Type: application/json" -d @"$TMPFILE"
rm -f "$TMPFILE"
