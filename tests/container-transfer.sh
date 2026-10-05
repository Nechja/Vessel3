#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT=${VESSEL3_CONTAINER_TEST_PORT:-9480}
ENDPOINT="http://127.0.0.1:$PORT"
CONTAINER_NAME="vessel3-ct-$$"
IMAGE_TAG="vessel3:test"

SCRATCH_DIR="$ROOT/scratch/ct-$$"
META_DIR="$SCRATCH_DIR/meta"
FAST_DIR="$SCRATCH_DIR/fast"
BULK_DIR="$SCRATCH_DIR/bulk"
WORK_DIR="$SCRATCH_DIR/work"
OCI_IMG="127.0.0.1:$PORT/container-test-img:1.0.0"

export NO_PROXY="127.0.0.1,localhost,::1,*"
export no_proxy="127.0.0.1,localhost,::1,*"
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy

cleanup() {
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
  docker rm -f "${CONTAINER_NAME}-restarted" >/dev/null 2>&1 || true
  docker rmi "$OCI_IMG" >/dev/null 2>&1 || true
  rm -rf "$SCRATCH_DIR" >/dev/null 2>&1 || true
}
trap cleanup EXIT

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is not available; skipping container transfer tests."
  exit 0
fi

if ! docker image inspect "$IMAGE_TAG" >/dev/null 2>&1; then
  echo "Building $IMAGE_TAG..."
  mkdir -p "$ROOT/scratch/docker-build"
  DOTNET_CLI_HOME="$ROOT/.dotnet-home" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    dotnet publish "$ROOT/Vessel3.Server/Vessel3.Server.csproj" -c Release -r linux-x64 --no-restore -o "$ROOT/scratch/docker-build"
  
  cat << 'DOCKERFILE' > "$ROOT/scratch/docker-build/Dockerfile"
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY vessel3 /app/vessel3
COPY libe_sqlite3.so /app/libe_sqlite3.so
RUN chmod +x /app/vessel3 && mkdir -p /data /storage/fast /storage/bulk
ENV VESSEL3_DATA=/data
ENV ASPNETCORE_URLS=http://0.0.0.0:9000
EXPOSE 9000
ENTRYPOINT ["/app/vessel3"]
DOCKERFILE
  docker build -t "$IMAGE_TAG" "$ROOT/scratch/docker-build"
fi

mkdir -p "$META_DIR" "$FAST_DIR" "$BULK_DIR" "$WORK_DIR"
chmod -R 777 "$SCRATCH_DIR"

echo "=== 1. Starting Containerized Vessel3 with Multi-Volume Storage ==="
docker run -d --name "$CONTAINER_NAME" \
  --user "$(id -u):$(id -g)" \
  -p "127.0.0.1:$PORT:9000" \
  -v "$META_DIR:/data" \
  -v "$FAST_DIR:/storage/fast" \
  -v "$BULK_DIR:/storage/bulk" \
  -e VESSEL3_DATA=/data \
  -e VESSEL3_VOLUMES="fast:/storage/fast:default:Ingest,bulk:/storage/bulk:vault:OnDemand" \
  -e VESSEL3_ACCESS_KEY=AKIATEST \
  -e VESSEL3_SECRET_KEY=secretkey1234567890 \
  -e VESSEL3_DOMAIN=localhost \
  -e VESSEL3_OCI_ENABLED=true \
  -e VESSEL3_WEBDAV_ENABLED=true \
  -e VESSEL3_AZURE_ENABLED=true \
  "$IMAGE_TAG"

echo "Waiting for container endpoint..."
ready=false
for _ in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/" 2>/dev/null || echo 000)
  if [ "$code" != "000" ]; then
    ready=true
    break
  fi
  sleep 0.25
done

if [ "$ready" != "true" ]; then
  echo "ERROR: Vessel3 container failed to become ready. Logs:" >&2
  docker logs "$CONTAINER_NAME" >&2
  exit 1
fi
echo "Container is online on $ENDPOINT."

echo "=== 2. Running AWS S3 Compatibility Battery against Container ==="
DOTNET_CLI_HOME="$ROOT/.dotnet-home" \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
DOTNET_NOLOGO=1 \
VESSEL3_ENDPOINT="$ENDPOINT" \
VESSEL3_ACCESS_KEY=AKIATEST \
VESSEL3_SECRET_KEY=secretkey1234567890 \
dotnet run --project "$ROOT/Vessel3.Tests.AwsCompatibility" -c Release --no-build --no-launch-profile

echo "=== 3. Uploading 10MB Payload via Dockerized AWS CLI ==="
head -c 10485760 /dev/urandom > "$WORK_DIR/test-10mb.bin"
ORIGINAL_SHA=$(sha256sum "$WORK_DIR/test-10mb.bin" | awk '{print $1}')
echo "Generated 10MB test payload with SHA-256: $ORIGINAL_SHA"

docker run --rm --net=host -v "$WORK_DIR:/work" \
  -e AWS_ACCESS_KEY_ID=AKIATEST \
  -e AWS_SECRET_ACCESS_KEY=secretkey1234567890 \
  -e AWS_DEFAULT_REGION=us-east-1 \
  amazon/aws-cli:latest --endpoint-url "$ENDPOINT" \
  s3 mb s3://transfer-test

docker run --rm --net=host -v "$WORK_DIR:/work" \
  -e AWS_ACCESS_KEY_ID=AKIATEST \
  -e AWS_SECRET_ACCESS_KEY=secretkey1234567890 \
  -e AWS_DEFAULT_REGION=us-east-1 \
  amazon/aws-cli:latest --endpoint-url "$ENDPOINT" \
  s3 cp /work/test-10mb.bin s3://transfer-test/test-10mb.bin

echo "=== 4. Verifying Hexagonal Storage Ingest Placement ==="
FAST_BLOB_COUNT=$(find "$FAST_DIR/blobs" -type f 2>/dev/null | wc -l)
BULK_BLOB_COUNT=$(find "$BULK_DIR/blobs" -type f 2>/dev/null | wc -l)

echo "Blobs on fast ingest volume: $FAST_BLOB_COUNT"
echo "Blobs on bulk vault volume:  $BULK_BLOB_COUNT"

if [ "$FAST_BLOB_COUNT" -eq 0 ]; then
  echo "ERROR: Expected blobs written to fast ingest volume, but found none!" >&2
  exit 1
fi
if [ "$BULK_BLOB_COUNT" -ne 0 ]; then
  echo "ERROR: Ingest write leaked into bulk on-demand volume ($BULK_BLOB_COUNT blobs found)!" >&2
  exit 1
fi
echo "Storage volume routing verified: all ingest writes routed strictly to Ingest volume."

echo "=== 5. Downloading and Verifying SHA-256 of S3 Transfer ==="
docker run --rm --net=host -v "$WORK_DIR:/work" \
  -e AWS_ACCESS_KEY_ID=AKIATEST \
  -e AWS_SECRET_ACCESS_KEY=secretkey1234567890 \
  -e AWS_DEFAULT_REGION=us-east-1 \
  amazon/aws-cli:latest --endpoint-url "$ENDPOINT" \
  s3 cp s3://transfer-test/test-10mb.bin /work/downloaded-10mb.bin

DOWNLOAD_SHA=$(sha256sum "$WORK_DIR/downloaded-10mb.bin" | awk '{print $1}')
if [ "$ORIGINAL_SHA" != "$DOWNLOAD_SHA" ]; then
  echo "ERROR: SHA-256 mismatch on S3 10MB payload: $ORIGINAL_SHA vs $DOWNLOAD_SHA" >&2
  exit 1
fi
echo "10MB S3 transfer verified: SHA-256 match ($DOWNLOAD_SHA)."

echo "=== 6. Azure Blob Storage Protocol Battery against Container ==="
DOTNET_CLI_HOME="$ROOT/.dotnet-home" \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
DOTNET_NOLOGO=1 \
VESSEL3_AZURE_ENDPOINT="$ENDPOINT/devstoreaccount1" \
dotnet run --project "$ROOT/Vessel3.Tests.AzureCompatibility" -c Release --no-build --no-launch-profile

echo "=== 7. WebDAV Collection and Multi-Megabyte Transfer Test ==="
curl -s -f -X MKCOL -u "AKIATEST:secretkey1234567890" "$ENDPOINT/dav/dav-transfer-test/"
curl -s -f -X PUT -u "AKIATEST:secretkey1234567890" --data-binary @"$WORK_DIR/test-10mb.bin" "$ENDPOINT/dav/dav-transfer-test/large-asset.bin"

PROPFIND_RESP=$(curl -s -f -X PROPFIND -u "AKIATEST:secretkey1234567890" -H "Depth: 1" "$ENDPOINT/dav/dav-transfer-test/")
if ! echo "$PROPFIND_RESP" | grep -q "large-asset.bin"; then
  echo "ERROR: WebDAV PROPFIND did not list large-asset.bin! Response: $PROPFIND_RESP" >&2
  exit 1
fi

curl -s -f -u "AKIATEST:secretkey1234567890" "$ENDPOINT/dav/dav-transfer-test/large-asset.bin" -o "$WORK_DIR/dav-downloaded-10mb.bin"
WEBDAV_SHA=$(sha256sum "$WORK_DIR/dav-downloaded-10mb.bin" | awk '{print $1}')
if [ "$ORIGINAL_SHA" != "$WEBDAV_SHA" ]; then
  echo "ERROR: WebDAV SHA-256 mismatch: $ORIGINAL_SHA vs $WEBDAV_SHA" >&2
  exit 1
fi
echo "WebDAV 10MB upload and download verified: SHA-256 match ($WEBDAV_SHA)."

echo "=== 8. OCI Container Registry Push & Pull against Containerized Vessel3 ==="
echo "secretkey1234567890" | docker login "127.0.0.1:$PORT" -u "AKIATEST" --password-stdin

cat << 'DOCKERFILE' > "$WORK_DIR/Dockerfile"
FROM scratch
COPY test-10mb.bin /data.bin
DOCKERFILE

docker build -t "$OCI_IMG" "$WORK_DIR"
docker push "$OCI_IMG"

docker rmi "$OCI_IMG"
docker pull "$OCI_IMG"
docker inspect "$OCI_IMG" | grep -q "container-test-img"
echo "OCI Container push and pull roundtrip verified."

echo "=== 9. Durability Across Container Recreation ==="
docker stop "$CONTAINER_NAME"
docker rm "$CONTAINER_NAME"

docker run -d --name "${CONTAINER_NAME}-restarted" \
  --user "$(id -u):$(id -g)" \
  -p "127.0.0.1:$PORT:9000" \
  -v "$META_DIR:/data" \
  -v "$FAST_DIR:/storage/fast" \
  -v "$BULK_DIR:/storage/bulk" \
  -e VESSEL3_DATA=/data \
  -e VESSEL3_VOLUMES="fast:/storage/fast:default:Ingest,bulk:/storage/bulk:vault:OnDemand" \
  -e VESSEL3_ACCESS_KEY=AKIATEST \
  -e VESSEL3_SECRET_KEY=secretkey1234567890 \
  -e VESSEL3_DOMAIN=localhost \
  -e VESSEL3_OCI_ENABLED=true \
  -e VESSEL3_WEBDAV_ENABLED=true \
  -e VESSEL3_AZURE_ENABLED=true \
  "$IMAGE_TAG"

for _ in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$ENDPOINT/" 2>/dev/null || echo 000)
  if [ "$code" != "000" ]; then break; fi
  sleep 0.25
done

docker run --rm --net=host -v "$WORK_DIR:/work" \
  -e AWS_ACCESS_KEY_ID=AKIATEST \
  -e AWS_SECRET_ACCESS_KEY=secretkey1234567890 \
  -e AWS_DEFAULT_REGION=us-east-1 \
  amazon/aws-cli:latest --endpoint-url "$ENDPOINT" \
  s3 cp s3://transfer-test/test-10mb.bin /work/after-restart-10mb.bin

RESTART_S3_SHA=$(sha256sum "$WORK_DIR/after-restart-10mb.bin" | awk '{print $1}')
if [ "$ORIGINAL_SHA" != "$RESTART_S3_SHA" ]; then
  echo "ERROR: SHA mismatch after container recreation on S3 payload!" >&2
  exit 1
fi

curl -s -f -u "AKIATEST:secretkey1234567890" "$ENDPOINT/dav/dav-transfer-test/large-asset.bin" -o "$WORK_DIR/after-restart-dav-10mb.bin"
RESTART_DAV_SHA=$(sha256sum "$WORK_DIR/after-restart-dav-10mb.bin" | awk '{print $1}')
if [ "$ORIGINAL_SHA" != "$RESTART_DAV_SHA" ]; then
  echo "ERROR: SHA mismatch after container recreation on WebDAV payload!" >&2
  exit 1
fi

docker rmi "$OCI_IMG"
docker pull "$OCI_IMG"
docker inspect "$OCI_IMG" | grep -q "container-test-img"

echo "Container restart durability verified: S3, WebDAV, and OCI image 100% intact."
echo "=== ALL CONTAINER CONNECT AND TRANSFER TESTS PASSED ==="
