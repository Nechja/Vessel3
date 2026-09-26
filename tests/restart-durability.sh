#!/usr/bin/env bash
# Verify the server's data survives a clean restart and that an in-flight
# multipart upload survives a hard kill (kill -9) of the server process.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT=${VESSEL3_RESTART_PORT:-9390}
ENDPOINT=${VESSEL3_ENDPOINT:-http://127.0.0.1:$PORT}
DATA_DIR=${VESSEL3_DATA:-/tmp/vessel3-restart-data}
SERVER_BIN=${SERVER_BIN:-$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3}
PROBE_PROJ=$ROOT/Vessel3.Tests.AwsCompatibility
STATE=${VESSEL3_PROBE_STATE:-/tmp/vessel3-probe-state.json}
SERVER_LOG=${VESSEL3_SERVER_LOG:-/tmp/vessel3-restart-server.log}

export VESSEL3_DATA=$DATA_DIR
export VESSEL3_ACCESS_KEY=AKIATEST
export VESSEL3_SECRET_KEY=secretkey1234567890
export VESSEL3_PROBE_STATE=$STATE
export VESSEL3_ENDPOINT=$ENDPOINT
export NO_PROXY="*"
export no_proxy="*"
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy

SERVER_PID=

cleanup() {
  if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

start_server() {
  for _ in $(seq 1 10); do
    "$SERVER_BIN" --urls "$ENDPOINT" >> "$SERVER_LOG" 2>&1 &
    SERVER_PID=$!
    for _ in $(seq 1 40); do
      code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/" 2>/dev/null || true)
      if [ "$code" != "000" ]; then return 0; fi
      if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
      sleep 0.25
    done
    wait "$SERVER_PID" 2>/dev/null || true
    sleep 0.5
  done
  echo "server failed to start; log:" >&2
  tail -50 "$SERVER_LOG" >&2
  return 1
}

stop_server_clean() {
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
    while kill -0 "$SERVER_PID" 2>/dev/null; do
      sleep 0.05
    done
    wait "$SERVER_PID" 2>/dev/null || true
    SERVER_PID=
  fi
  sleep 1
}

stop_server_hard() {
  if [ -n "$SERVER_PID" ]; then
    kill -9 "$SERVER_PID" 2>/dev/null || true
    while kill -0 "$SERVER_PID" 2>/dev/null; do
      sleep 0.05
    done
    wait "$SERVER_PID" 2>/dev/null || true
    SERVER_PID=
  fi
  sleep 1
}

run_phase() {
  dotnet run --project "$PROBE_PROJ" -c Release --no-launch-profile -- "$1"
}

rm -rf "$DATA_DIR" "$STATE" "$SERVER_LOG"
mkdir -p "$DATA_DIR"

echo "=== restart durability ==="
start_server
run_phase restart-write
stop_server_clean
start_server
run_phase restart-verify
stop_server_clean

echo
echo "=== mid-multipart-upload crash recovery ==="
start_server
run_phase crash-multipart-write
stop_server_hard
start_server
run_phase crash-multipart-finish
stop_server_clean

echo
echo "ALL DURABILITY TESTS PASSED"
