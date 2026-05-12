#!/usr/bin/env bash
# Exercise the server's no-auth code paths: AwsChunkedStream w/o signing, strict
# bucket-name validation, and malformed VersioningConfiguration XML. The server
# is launched without VESSEL3_ACCESS_KEY so SigV4Middleware uses AlwaysPassVerifier.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
ENDPOINT=${VESSEL3_ANON_ENDPOINT:-http://127.0.0.1:9101}
DATA_DIR=${VESSEL3_DATA:-/tmp/vessel3-anon-data}
SERVER_BIN=${SERVER_BIN:-$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3}
PROBE_PROJ=$ROOT/Vessel3.RealClient
SERVER_LOG=${VESSEL3_SERVER_LOG:-/tmp/vessel3-anon-server.log}

unset VESSEL3_ACCESS_KEY VESSEL3_SECRET_KEY
export VESSEL3_DATA=$DATA_DIR
export VESSEL3_ANON_ENDPOINT=$ENDPOINT

SERVER_PID=

cleanup() {
  if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

rm -rf "$DATA_DIR" "$SERVER_LOG"
mkdir -p "$DATA_DIR"

"$SERVER_BIN" --urls "$ENDPOINT" >> "$SERVER_LOG" 2>&1 &
SERVER_PID=$!
for _ in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/" || echo 000)
  if [ "$code" = "200" ]; then break; fi
  sleep 0.25
done

dotnet run --project "$PROBE_PROJ" -c Release --no-launch-profile -- anon
