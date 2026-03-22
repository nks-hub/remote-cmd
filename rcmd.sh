#!/bin/bash
TOKEN="${REMOTECMD_TOKEN:?Error: REMOTECMD_TOKEN environment variable is not set}"
URL="${REMOTECMD_URL:-http://localhost:7890}"
TIMEOUT="${2:-30}"
curl -s -X POST "$URL/api/exec" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $TOKEN" \
    -d "{\"command\":\"$1\",\"timeoutSeconds\":$TIMEOUT}"
