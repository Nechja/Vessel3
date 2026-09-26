#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT=${VESSEL3_WEBSITE_PORT:-9350}
ENDPOINT=http://127.0.0.1:$PORT
DATA_DIR=${VESSEL3_WEBSITE_DATA:-/tmp/vessel3-website-data-$$}
SERVER_LOG=${VESSEL3_WEBSITE_LOG:-/tmp/vessel3-website-server.log}
SERVER_BIN=${SERVER_BIN:-$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3}

export VESSEL3_DATA=$DATA_DIR
export VESSEL3_ACCESS_KEY=AKIATEST
export VESSEL3_SECRET_KEY=secretkey1234567890
export VESSEL3_ENDPOINT=$ENDPOINT
export VESSEL3_DOMAIN=localhost
export NO_PROXY="127.0.0.1,localhost,::1"
export no_proxy="127.0.0.1,localhost,::1"

SERVER_PID=
cleanup() {
  if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$DATA_DIR"
}
trap cleanup EXIT

echo "building server (Release)..."
dotnet build "$ROOT/Vessel3.Server" -c Release --nologo -v q > /dev/null

rm -rf "$DATA_DIR" "$SERVER_LOG"
mkdir -p "$DATA_DIR"

"$SERVER_BIN" --urls "$ENDPOINT" >> "$SERVER_LOG" 2>&1 &
SERVER_PID=$!
for _ in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/" || echo 000)
  if [ "$code" != "000" ]; then break; fi
  sleep 0.25
done

dotnet run --project "$ROOT/Vessel3.Tests.AwsCompatibility" -c Release --no-launch-profile -- website
