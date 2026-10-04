#!/usr/bin/env bash
# Fast real Docker client verification for Vessel3 Container Repos.
# Boots Vessel3 server, logs in with Docker CLI, builds a minimal scratch image,
# pushes it, checks catalog & tags, deletes local image, pulls it back, and inspects.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT=${VESSEL3_OCI_TEST_PORT:-9350}
ENDPOINT=http://127.0.0.1:$PORT
DATA_DIR=${VESSEL3_OCI_DATA:-$ROOT/scratch/oci-test-data-$$}
SERVER_LOG=${VESSEL3_OCI_LOG:-/tmp/vessel3-oci-server.log}
SERVER_BIN=${SERVER_BIN:-}
IMAGE_NAME="127.0.0.1:$PORT/drummer:1.0.0"
BUILD_DIR="$DATA_DIR/build"

export VESSEL3_DATA=$DATA_DIR
export VESSEL3_ACCESS_KEY=admin
export VESSEL3_SECRET_KEY=adminpassword123
export VESSEL3_OCI_ENABLED=true
export NO_PROXY="127.0.0.1,localhost,::1,*"
export no_proxy="127.0.0.1,localhost,::1,*"
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy

SERVER_PID=
cleanup() {
  echo "=== Cleaning up ==="
  if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  docker rmi "$IMAGE_NAME" 2>/dev/null || true
  rm -rf "$DATA_DIR"
  echo "=== Done ==="
}
trap cleanup EXIT

if ! command -v docker >/dev/null 2>&1; then
  if [ "${CI:-}" = "true" ]; then
    echo "ERROR: docker is required in CI but not installed or not in PATH." >&2
    exit 1
  fi
  echo "Docker is not installed or not in PATH; skipping real Docker client test."
  exit 0
fi

if [ -z "$SERVER_BIN" ]; then
  if [ -x "$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3" ]; then
    SERVER_BIN="$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3"
  elif [ -x "$ROOT/Vessel3.Server/bin/Debug/net10.0/vessel3" ]; then
    SERVER_BIN="$ROOT/Vessel3.Server/bin/Debug/net10.0/vessel3"
  else
    echo "building server..."
    dotnet build "$ROOT/Vessel3.Server" -c Release --nologo -v q > /dev/null
    SERVER_BIN="$ROOT/Vessel3.Server/bin/Release/net10.0/vessel3"
  fi
fi

rm -rf "$DATA_DIR" "$SERVER_LOG"
mkdir -p "$BUILD_DIR"

"$SERVER_BIN" --urls "$ENDPOINT" >> "$SERVER_LOG" 2>&1 &
SERVER_PID=$!

server_up=false
for _ in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/v2/" 2>/dev/null || echo 000)
  if [ "$code" = "401" ] || [ "$code" = "200" ]; then
    server_up=true
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
  sleep 0.25
done

if [ "$server_up" != "true" ]; then
  echo "ERROR: Vessel3 server failed to start on $ENDPOINT. Server log:" >&2
  cat "$SERVER_LOG" >&2
  exit 1
fi

echo "=== 1. Docker login ==="
echo "adminpassword123" | docker login "127.0.0.1:$PORT" -u "admin" --password-stdin

echo "=== 2. Build scratch image ==="
echo "Hello from Vessel3 Container Repos!" > "$BUILD_DIR/hello.txt"
cat << 'DOCKERFILE' > "$BUILD_DIR/Dockerfile"
FROM scratch
COPY hello.txt /hello.txt
DOCKERFILE
docker build -t "$IMAGE_NAME" "$BUILD_DIR"

echo "=== 3. Push image ==="
docker push "$IMAGE_NAME"

echo "=== 4. Verify catalog & tags ==="
TOKEN=$(curl -s -u "admin:adminpassword123" "$ENDPOINT/v2/token?service=127.0.0.1:$PORT&scope=repository:drummer:pull" | grep -o '"token":"[^"]*' | cut -d'"' -f4)
CATALOG=$(curl -s -H "Authorization: Bearer $TOKEN" "$ENDPOINT/v2/_catalog")
echo "  Catalog: $CATALOG"
if ! echo "$CATALOG" | grep -q "drummer"; then
  echo "ERROR: Expected repository 'drummer' in catalog!" >&2
  exit 1
fi

TAGS=$(curl -s -H "Authorization: Bearer $TOKEN" "$ENDPOINT/v2/drummer/tags/list")
echo "  Tags:    $TAGS"
if ! echo "$TAGS" | grep -q '"1.0.0"'; then
  echo "ERROR: Expected tag '1.0.0' in tags list!" >&2
  exit 1
fi

echo "=== 5. Remove local image ==="
docker rmi "$IMAGE_NAME"

echo "=== 6. Pull image back from Vessel3 ==="
docker pull "$IMAGE_NAME"

echo "=== 7. Inspect image ==="
docker inspect "$IMAGE_NAME" | grep -E '"Id"|"RepoTags"'

echo "=== 8. Manifest delete and lifecycle verification ==="
DIGEST=$(curl -s -I -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json" "$ENDPOINT/v2/drummer/manifests/1.0.0" | tr -d '\r' | grep -i '^docker-content-digest:' | awk '{print $2}')
if [ -n "$DIGEST" ]; then
  echo "  Deleting manifest by digest $DIGEST..."
  WRITE_TOKEN=$(curl -s -u "admin:adminpassword123" "$ENDPOINT/v2/token?service=127.0.0.1:$PORT&scope=repository:drummer:pull,push" | grep -o '"token":"[^"]*' | cut -d'"' -f4)
  DEL_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X DELETE -H "Authorization: Bearer $WRITE_TOKEN" "$ENDPOINT/v2/drummer/manifests/$DIGEST")
  if [ "$DEL_STATUS" != "202" ]; then
    echo "ERROR: Expected DELETE manifest to return 202, got $DEL_STATUS" >&2
    exit 1
  fi
  echo "  Manifest deleted successfully (HTTP 202)."
  
  # Remove image locally and confirm pull now fails
  docker rmi "$IMAGE_NAME" >/dev/null 2>&1 || true
  if docker pull "$IMAGE_NAME" >/dev/null 2>&1; then
    echo "ERROR: docker pull succeeded after manifest deletion!" >&2
    exit 1
  fi
  echo "  docker pull rejected after deletion as expected."
fi

echo "=== PASS: Real Docker client test passed completely! ==="
