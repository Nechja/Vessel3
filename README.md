# Vessel3

[![probe](https://github.com/Nechja/Vessel3/actions/workflows/probe.yml/badge.svg)](https://github.com/Nechja/Vessel3/actions/workflows/probe.yml)
[![container](https://github.com/Nechja/Vessel3/actions/workflows/container.yml/badge.svg)](https://github.com/Nechja/Vessel3/actions/workflows/container.yml)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache_2.0-blue.svg)](LICENSE)

A single-binary, S3-compatible object server.

Built for (my) homelab and single app use. This isn't built for multi-tenant setups or workloads that need advanced policies.


## What it is

- Made with .NET 10, because why not.
- The S3 wire protocol. AWS CLI, MinIO `mc`, boto3, and the AWS SDKs talk to it without code changes.
- SigV4-signed requests, including the `STREAMING-UNSIGNED-PAYLOAD-TRAILER` mode boto3 uses by default.
- Lifecycle rules (current expiration, noncurrent version expiration, expired delete marker pruning), multipart uploads, presigned URLs, versioning, Object Lock, tagging, per-version retention and legal hold, conditional reads and writes, range and suffix-range GETs, per-object checksums (CRC32, CRC32C, SHA1, SHA256), `EncodingType=url`, `GetObjectAttributes`.
- Crash-safe persistence. Every write fsyncs. The event log is the source of truth; the SQLite index is rebuildable from it after any crash, including mid-write.
- Atomic overwrites. A reader looking up a key during a concurrent same-key overwrite sees the old value or the new value, never absence.
- Cool.

## What it isn't

- Not a cluster.
- Not multi-tenant. One static access key, plus short-lived session credentials from an OIDC exchange. No IAM, no policies. ACL endpoints return a bucket-owner stub.
- Not a webserver. It serves bytes well. Put Caddy or nginx in front for HTML, TLS, and rate-limiting.
- Not tuned for thousands of concurrent uploaders.
- Not something made to be the best thing you've ever used.

## Install

### Binary

Grab a release binary. Each release ships two variants per platform: the default
is the S3 API alone, and `-ui` embeds the web UI (served at `/_ui`, gated by the
access keys).

```sh
curl -L https://github.com/Nechja/Vessel3/releases/latest/download/vessel3-v0.1.0-linux-x64.tar.gz | tar -xz
./vessel3 --urls http://127.0.0.1:9000
```

### Container

Images follow the same split: `latest` is the bare S3 server, `latest-ui` adds
the web UI (every tag has a `-ui` twin).

```sh
docker run -p 9000:9000 \
  -e VESSEL3_ACCESS_KEY=AKIA... \
  -e VESSEL3_SECRET_KEY=... \
  -v vessel3-data:/data \
  -e VESSEL3_DATA=/data \
  ghcr.io/nechja/vessel3:latest
```

### Kubernetes

The image runs as a non-root user (uid 1654). When a `PersistentVolumeClaim` is mounted
over `/data`, the kubelet creates the mount root as `root:root`, so the process can't write
to it. Set `fsGroup` so the kubelet chowns the volume to the runtime uid on mount:

```yaml
spec:
  securityContext:
    fsGroup: 1654
  containers:
    - name: vessel3
      image: ghcr.io/nechja/vessel3:latest
      env:
        - { name: VESSEL3_DATA, value: /data }
      volumeMounts:
        - { name: data, mountPath: /data }
```

Without it, Vessel3 logs that `VESSEL3_DATA` is not writable and exits at startup.

### From source

```sh
dotnet publish Vessel3.Server -c Release -r linux-x64 --self-contained
```

Requires .NET 10 SDK.

## Configure

All via environment variables. No config file.

| Variable | Default | Meaning |
|---|---|---|
| `VESSEL3_DATA` | next to the binary | Data root (blobs, index, log). Persist this. |
| `VESSEL3_ACCESS_KEY` | unset -> auth disabled | SigV4 access key id. |
| `VESSEL3_SECRET_KEY` | unset -> auth disabled | SigV4 secret. |
| `VESSEL3_REGION` | `us-east-1` | Region string used for SigV4 verification. |
| `VESSEL3_METRICS_TOKEN` | unset | If set, `/metrics` accepts requests from any IP that present `Authorization: Bearer <token>`. Loopback always works without the token. |
| `VESSEL3_LIFECYCLE_INTERVAL_SECONDS` | `3600` | How often the lifecycle sweep runs. `0` disables it. |
| `VESSEL3_COMPACT_INTERVAL_SECONDS` | `3600` | How often the compaction sweep runs. `0` disables it. |
| `VESSEL3_COMPACT_THRESHOLD_BYTES` | `67108864` | Minimum event log size in bytes before `PUT /_admin/compact` compacts. |
| `VESSEL3_GC_MAX_WAIT_SECONDS` | `120` | Max seconds GC waits for in-flight writes to complete before aborting sweep. |
| `VESSEL3_METRICS_ALLOW_ANONYMOUS` | `false` | If `true`, `/metrics` is fully public. Overrides token and loopback restrictions. Don't enable on a public-facing box. |
| `VESSEL3_SLOW_REQUEST_MS` | `1000` | Logs requests exceeding this threshold with per-stage timing. |
| `VESSEL3_OIDC_ISSUER` | unset | OIDC issuer URL. Setting it enables the web identity exchange below. |
| `VESSEL3_OIDC_CLIENT_ID` | unset | Client id tokens must be issued for. Required with the issuer. |
| `VESSEL3_OIDC_AUDIENCE` | unset | Extra audience accepted alongside the client id. |
| `VESSEL3_OIDC_REQUIRE_CLAIM` | unset | `name=value`. Tokens must carry that claim, as a string or an array member, or the exchange is refused. |

The listen address comes from Kestrel's `--urls` flag in the usual ASP.NET way.

## Use

Point any S3 client at it.

```sh
export AWS_ACCESS_KEY_ID=AKIA...
export AWS_SECRET_ACCESS_KEY=...
aws --endpoint-url http://127.0.0.1:9000 s3 mb s3://photos
aws --endpoint-url http://127.0.0.1:9000 s3 cp ./cat.jpg s3://photos/
```

## OIDC

With `VESSEL3_OIDC_ISSUER` and `VESSEL3_OIDC_CLIENT_ID` set, an ID token or JWT access token from that issuer can be traded for short-lived S3 credentials using the STS `AssumeRoleWithWebIdentity` shape. Any AWS SDK's web identity provider works against it; `RoleArn` is accepted and ignored.

```sh
aws --endpoint-url http://127.0.0.1:9000 sts assume-role-with-web-identity \
  --role-arn arn:aws:iam::0:role/vessel3 --role-session-name me \
  --web-identity-token "$TOKEN" --duration-seconds 3600
```

With the `-ui` build, the web has a UI signs in through the issuer using authorization code with PKCE, trades the ID token at the same exchange, and signs its S3 calls with the session. Register a public client at the issuer with `<origin>/_ui/` as its redirect URI, and the same URI as a post-logout redirect.

Sessions default to one hour, range 15 minutes to 12 hours, and live in memory, so a restart ends them. Tokens are verified against the issuer's JWKS (ES256 and RS256), with the key set refetched when an unknown `kid` appears. The static access key stays valid alongside sessions, and auth is enforced when either is configured.

## Durability

If the process dies mid-write, Vessel3 comes back with a consistent view and can rebuild its index from the event log.

- Every PUT lands in a temp file, gets fsync'd, then moved into place.
- Every version is appended to a per-bucket event log; the log file is fsync'd before the call returns.
- The SQLite index runs in WAL mode and is rebuildable from the snapshot plus the log tail, even after a wipe.
- Compaction checkpoints the index into `snapshot.db` and truncates the log; both land via fsync + atomic rename, so a crash at any point leaves a consistent pair.
- Concurrent overwrites are atomic from the reader's point of view.
- Mid-write crashes leave at most a partial trailing event in the log, which is truncated on next open.

The kill-9 and replay paths are covered by automated tests. The "drive lies about fsync" failure mode is not — that's a hardware and kernel layer Vessel3 can't test from inside.

## Storage layout

```
VESSEL3_DATA/
  blobs/
    aa/bb/<sha256>              content-addressed object bytes
    tmp/<guid>                  in-flight writes prior to atomic rename
  buckets/<name>/
    log                         append-only event log, truncated by compaction
    index.db                    SQLite catalog (rebuildable from snapshot + log)
    snapshot.db                 checkpoint of the catalog, written by compaction
    versioning                  bucket versioning state
    object-lock.json            bucket object-lock config
    lifecycle.json              bucket lifecycle configuration
  uploads/<upload-id>/          in-flight multipart parts
```

## High-churn workloads (Loki, backups with retention)

Vessel3 works as a Loki object store out of the box.

- **Lifecycle expiration handles retention.** Both unversioned and versioned buckets support standard S3 lifecycle configurations. On versioned buckets, configure `<NoncurrentVersionExpiration>` and `<ExpiredObjectDeleteMarker>` to automatically prune older versions and lone delete markers. Unversioned buckets hard-delete expired objects during the lifecycle sweep.
- **Compaction keeps the event log small.** Compaction keeps the event log proportional to recent activity instead of all-time history. The background compaction service runs automatically, or can be triggered via `PUT /_admin/compact`.
- **Blob GC reclaims unreferenced data.** `PUT /_admin/gc` sweeps unreferenced blobs and abandoned temp files, respecting active writes and Object Lock retention.

Bulk deletes (`DeleteObjects`) commit each request as a single log record with one fsync, so 1000-key retention sweeps complete in one disk round-trip.

## Limits

- PUT body up to 5 GiB. Multipart part up to 5 GiB.
- Multipart non-last parts must be at least 5 MiB.
- Up to 10 tags per object.

## License

Apache 2.0. See [LICENSE](LICENSE).

## AI Use

AI's did design a lot of the testing in this repo, and assist with the readme.
