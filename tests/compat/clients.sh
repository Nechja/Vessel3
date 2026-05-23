#!/usr/bin/env bash
set -u

HOST=${VESSEL3_HOST:-host.docker.internal}
PORT=${VESSEL3_PORT:-9000}
AK=${VESSEL3_ACCESS_KEY:-AKIATEST}
SK=${VESSEL3_SECRET_KEY:-secretkey1234567890}
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORK=${VESSEL3_COMPAT_WORK:-/tmp/v3compat-$$}
mkdir -p "$WORK/in" "$WORK/out"

PASS=0
FAIL=0
FAILS=()
ok() { PASS=$((PASS+1)); echo "  PASS  $1"; }
no() { FAIL=$((FAIL+1)); FAILS+=("$1"); echo "  FAIL  $1 :: $2"; }

cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

md5sum_file() {
  if command -v md5 >/dev/null 2>&1; then md5 -q "$1"
  else md5sum "$1" | awk '{print $1}'
  fi
}

DOCKER_ARGS=(--rm --add-host=host.docker.internal:host-gateway -v "$WORK:/work")

awscli() {
  docker run "${DOCKER_ARGS[@]}" \
    -e AWS_ACCESS_KEY_ID="$AK" \
    -e AWS_SECRET_ACCESS_KEY="$SK" \
    -e AWS_DEFAULT_REGION=us-east-1 \
    -e AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED \
    -e AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED \
    amazon/aws-cli:latest --endpoint-url "http://$HOST:$PORT" "$@"
}

mcclient() {
  docker run "${DOCKER_ARGS[@]}" \
    -e MC_HOST_v3="http://$AK:$SK@$HOST:$PORT" \
    --entrypoint mc minio/mc:latest "$@"
}

RCLONE_CONF="$WORK/rclone.conf"
cat > "$RCLONE_CONF" <<EOF
[v3]
type = s3
provider = Other
access_key_id = $AK
secret_access_key = $SK
endpoint = http://$HOST:$PORT
region = us-east-1
force_path_style = true
EOF

rcloneclient() {
  docker run "${DOCKER_ARGS[@]}" \
    -v "$RCLONE_CONF:/config/rclone/rclone.conf:ro" \
    rclone/rclone:latest "$@"
}

dd if=/dev/urandom of="$WORK/in/small.bin" bs=1K count=10 2>/dev/null
dd if=/dev/urandom of="$WORK/in/big.bin" bs=1M count=20 2>/dev/null
SMALL_MD5=$(md5sum_file "$WORK/in/small.bin")
BIG_MD5=$(md5sum_file "$WORK/in/big.bin")
mkdir -p "$WORK/in/syncdir"
for i in 1 2 3 4 5; do head -c 4096 /dev/urandom > "$WORK/in/syncdir/f$i.bin"; done

echo "=== AWS CLI ==="
awscli s3 mb s3://cli-aws >/dev/null 2>&1 && ok "mb" || no "awscli mb" "$?"
awscli s3 cp /work/in/small.bin s3://cli-aws/small.bin >/dev/null 2>&1 && ok "put small" || no "awscli put small" "$?"
awscli s3 cp /work/in/big.bin s3://cli-aws/big.bin >/dev/null 2>&1 && ok "put 20MB" || no "awscli put 20MB" "$?"
awscli s3 cp s3://cli-aws/big.bin /work/out/aws-big.bin >/dev/null 2>&1 && ok "get 20MB" || no "awscli get 20MB" "$?"
[ "$(md5sum_file "$WORK/out/aws-big.bin")" = "$BIG_MD5" ] && ok "20MB md5 match" || no "awscli 20MB md5 match" "got $(md5sum_file "$WORK/out/aws-big.bin")"
awscli s3 sync /work/in/syncdir s3://cli-aws/sync/ >/dev/null 2>&1 && ok "sync up" || no "awscli sync up" "$?"
mkdir -p "$WORK/out/aws-sync"
awscli s3 sync s3://cli-aws/sync/ /work/out/aws-sync/ >/dev/null 2>&1 && ok "sync down" || no "awscli sync down" "$?"
diff -r "$WORK/in/syncdir" "$WORK/out/aws-sync" >/dev/null 2>&1 && ok "sync identical" || no "awscli sync identical" "differ"
awscli s3api list-multipart-uploads --bucket cli-aws >/dev/null 2>&1 && ok "list MPUs" || no "awscli list MPUs" "$?"
awscli s3api put-object-tagging --bucket cli-aws --key small.bin --tagging 'TagSet=[{Key=env,Value=prod}]' >/dev/null 2>&1 && ok "tagging put" || no "awscli tagging put" "$?"
awscli s3api get-object-tagging --bucket cli-aws --key small.bin 2>&1 | grep -q env && ok "tagging get" || no "awscli tagging get" "no env tag"
awscli s3 rb s3://cli-aws --force >/dev/null 2>&1 && ok "rb force" || no "awscli rb force" "$?"

echo
echo "=== MinIO mc ==="
mcclient mb v3/cli-mc >/dev/null 2>&1 && ok "mb" || no "mc mb" "$?"
mcclient cp /work/in/small.bin v3/cli-mc/small.bin >/dev/null 2>&1 && ok "cp put small" || no "mc cp small" "$?"
mcclient cp /work/in/big.bin v3/cli-mc/big.bin >/dev/null 2>&1 && ok "cp put 20MB" || no "mc cp 20MB" "$?"
mcclient cp v3/cli-mc/big.bin /work/out/mc-big.bin >/dev/null 2>&1 && ok "cp get 20MB" || no "mc get 20MB" "$?"
[ "$(md5sum_file "$WORK/out/mc-big.bin")" = "$BIG_MD5" ] && ok "20MB md5 match" || no "mc 20MB md5 match" "got $(md5sum_file "$WORK/out/mc-big.bin")"
mcclient stat v3/cli-mc/small.bin >/dev/null 2>&1 && ok "stat" || no "mc stat" "$?"
mcclient tag set v3/cli-mc/small.bin "env=prod&owner=core" >/dev/null 2>&1 && ok "tag set" || no "mc tag set" "$?"
mcclient tag list v3/cli-mc/small.bin 2>&1 | grep -q env && ok "tag list" || no "mc tag list" "no env"
mcclient mirror /work/in/syncdir v3/cli-mc/mirror/ >/dev/null 2>&1 && ok "mirror up" || no "mc mirror up" "$?"
mkdir -p "$WORK/out/mc-mirror"
mcclient mirror v3/cli-mc/mirror/ /work/out/mc-mirror/ >/dev/null 2>&1 && ok "mirror down" || no "mc mirror down" "$?"
diff -r "$WORK/in/syncdir" "$WORK/out/mc-mirror" >/dev/null 2>&1 && ok "mirror identical" || no "mc mirror identical" "differ"
mcclient rm --recursive --force v3/cli-mc >/dev/null 2>&1 && ok "rm recursive" || no "mc rm recursive" "$?"
mcclient rb v3/cli-mc >/dev/null 2>&1 && ok "rb" || no "mc rb" "$?"

echo
echo "=== rclone ==="
rcloneclient mkdir v3:cli-rclone >/dev/null 2>&1 && ok "mkdir" || no "rclone mkdir" "$?"
rcloneclient copy /work/in/small.bin v3:cli-rclone/ >/dev/null 2>&1 && ok "copy small" || no "rclone copy small" "$?"
rcloneclient copy /work/in/big.bin v3:cli-rclone/ >/dev/null 2>&1 && ok "copy 20MB" || no "rclone copy 20MB" "$?"
mkdir -p "$WORK/out/rclone"
rcloneclient copy v3:cli-rclone/big.bin /work/out/rclone/ >/dev/null 2>&1 && ok "get 20MB" || no "rclone get 20MB" "$?"
[ "$(md5sum_file "$WORK/out/rclone/big.bin")" = "$BIG_MD5" ] && ok "20MB md5 match" || no "rclone 20MB md5 match" "got $(md5sum_file "$WORK/out/rclone/big.bin" 2>/dev/null)"
rcloneclient md5sum v3:cli-rclone/big.bin 2>&1 | awk '{print $1}' | grep -q "$BIG_MD5" && ok "remote md5sum matches" || no "rclone remote md5sum" "mismatch"
rcloneclient ls v3:cli-rclone >/dev/null 2>&1 && ok "ls" || no "rclone ls" "$?"
rcloneclient sync /work/in/syncdir v3:cli-rclone/sync/ >/dev/null 2>&1 && ok "sync up" || no "rclone sync up" "$?"
mkdir -p "$WORK/out/rclone-sync"
rcloneclient sync v3:cli-rclone/sync/ /work/out/rclone-sync/ >/dev/null 2>&1 && ok "sync down" || no "rclone sync down" "$?"
diff -r "$WORK/in/syncdir" "$WORK/out/rclone-sync" >/dev/null 2>&1 && ok "sync identical" || no "rclone sync identical" "differ"
rcloneclient check /work/in/syncdir v3:cli-rclone/sync/ >/dev/null 2>&1 && ok "check" || no "rclone check" "exit $?"
rcloneclient purge v3:cli-rclone >/dev/null 2>&1 && ok "purge" || no "rclone purge" "$?"

echo
echo "============================================"
echo "TOTAL: PASS=$PASS FAIL=$FAIL"
if [ $FAIL -gt 0 ]; then
  printf '  FAIL: %s\n' "${FAILS[@]}"
fi
exit $FAIL
