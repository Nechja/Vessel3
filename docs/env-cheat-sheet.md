# Environment Variables Cheat Sheet

All configuration in Vessel3 is supplied via environment variables. No configuration file is required.

---

## Quick Reference Table

| Variable | Type | Default | Category | Purpose |
|---|---|---|---|---|
| `VESSEL3_DATA` | Path | `data` next to binary | Storage | Root data directory for blobs, buckets, uploads, and IAM database. |
| `VESSEL3_ACCESS_KEY` | String | *unset* (auth disabled) | Auth / IAM | Root SigV4 access key ID. Automatically provisions the bootstrap `admin` user. |
| `VESSEL3_SECRET_KEY` | String | *unset* (auth disabled) | Auth / IAM | Secret key paired with `VESSEL3_ACCESS_KEY`. |
| `VESSEL3_REGION` | String | `us-east-1` | S3 Protocol | Region identifier used for SigV4 signature verification and `GetBucketLocation`. |
| `VESSEL3_DOMAIN` | List (csv) | *unset* (path-style only) | Routing | Base domains for virtual-host routing (`s3.local,localhost`). `admin.<domain>` routes to the Web UI. |
| `VESSEL3_METRICS_TOKEN` | String | *unset* | Telemetry | Bearer token required to access `/metrics` from non-loopback IP addresses. |
| `VESSEL3_METRICS_ALLOW_ANONYMOUS` | Boolean | `false` | Telemetry | When `true`, `/metrics` is accessible without authentication from any network. |
| `VESSEL3_SLOW_REQUEST_MS` | Integer | `1000` | Telemetry | Requests exceeding this latency threshold log detailed stage-by-stage execution breakdowns. |
| `VESSEL3_LIFECYCLE_INTERVAL_SECONDS` | Integer | `3600` | Maintenance | Background lifecycle expiration sweep interval in seconds. `0` disables background sweeps. |
| `VESSEL3_COMPACT_INTERVAL_SECONDS` | Integer | `3600` | Maintenance | Background SQLite WAL checkpoint and event log compaction interval. `0` disables sweeps. |
| `VESSEL3_COMPACT_THRESHOLD_BYTES` | Integer | `67108864` (64 MiB) | Maintenance | Minimum event log size before compaction is executed. |
| `VESSEL3_GC_MAX_WAIT_SECONDS` | Integer | `120` | Maintenance | Maximum duration Blob GC waits for active concurrent writers before timing out. |
| `VESSEL3_OIDC_ISSUER` | URL | *unset* | OIDC | OpenID Connect discovery endpoint (e.g., `https://auth.example.com/realms/master`). |
| `VESSEL3_OIDC_CLIENT_ID` | String | *unset* | OIDC | Client ID that tokens must be issued for. Required when `VESSEL3_OIDC_ISSUER` is set. |
| `VESSEL3_OIDC_AUDIENCE` | String | *unset* | OIDC | Optional secondary audience allowed in JWTs. |
| `VESSEL3_OIDC_REQUIRE_CLAIM` | `key=val` | *unset* | OIDC | Restricts login to tokens containing `key=val` (as a string or array element). |
| `VESSEL3_CONTAINER_REPOS_ENABLED` | Boolean | `true` | Container Repos | Enables OCI / Docker Registry v2 container repository protocol (`/v2/...`). Also accepts `VESSEL3_OCI_ENABLED`. |
| `ASPNETCORE_URLS` | URLs | `http://127.0.0.1:9000` | Server | Kestrel listen addresses (e.g. `http://0.0.0.0:9000`). Alternately set via CLI `--urls`. |

---

## Category Breakdown

### 1. Storage & Bootstrapping
- **`VESSEL3_DATA`**: Persist this directory across restarts. Subdirectories:
  - `blobs/`: Content-addressed immutable object bodies (`aa/bb/<sha256>`).
  - `buckets/<bucket>/`: Per-bucket SQLite catalogs (`index.db`), append-only logs (`log`), and configuration files.
  - `iam/iam.db`: Multi-user IAM database storing users and access keys.
  - `uploads/`: In-flight multipart part chunks.
- **`VESSEL3_ACCESS_KEY` & `VESSEL3_SECRET_KEY`**:
  - When provided, Vessel3 automatically creates an `admin` user in `iam/iam.db` (if missing) and inserts this key pair as the bootstrap admin credential.
  - If both are omitted and OIDC is not configured, Vessel3 operates in **Unauthenticated Mode** (all callers map to `system:Admin`).

### 2. Networking & Virtual-Host Routing
- **`VESSEL3_DOMAIN`**: Accepts comma or semicolon-separated base domains:
  - Request with `Host: my-bucket.s3.example.com` resolves directly to bucket `my-bucket`.
  - Request with `Host: admin.s3.example.com` routes to the embedded Web UI (`/_ui`).
  - Request with IP address or unmapped host defaults to standard S3 path-style routing (`http://host:9000/my-bucket/key`).

### 3. Maintenance Sweeps
- **Lifecycle Sweeper** (`VESSEL3_LIFECYCLE_INTERVAL_SECONDS`): Sweeps expired objects and lone delete markers according to bucket lifecycle rules.
- **Compactor** (`VESSEL3_COMPACT_INTERVAL_SECONDS` & `VESSEL3_COMPACT_THRESHOLD_BYTES`): Flushes SQLite WAL to `snapshot.db` and truncates the write log, bounding disk space on high-churn workloads.
- **Blob GC** (`VESSEL3_GC_MAX_WAIT_SECONDS`): Purges unreferenced blobs and orphan multipart uploads.

### 4. Telemetry & Metrics
- Prometheus metrics are published at `GET /metrics`.
- Loopback IPs (`127.0.0.1`, `::1`) bypass authentication by default. External scrapers must provide `Authorization: Bearer <VESSEL3_METRICS_TOKEN>` unless `VESSEL3_METRICS_ALLOW_ANONYMOUS=true` is set.

---

## Deployment Profiles

### Profile A: Minimal Homelab `.env`
No authentication, runs on localhost:
```env
ASPNETCORE_URLS=http://127.0.0.1:9000
VESSEL3_DATA=/var/lib/vessel3
```

### Profile B: Production Multi-User with Custom Domain
Admin bootstrap, virtual hosts, metrics security, and background maintenance:
```env
ASPNETCORE_URLS=http://0.0.0.0:9000
VESSEL3_DATA=/data
VESSEL3_ACCESS_KEY=V3AKADMINEXAMPLE1234
VESSEL3_SECRET_KEY=9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b
VESSEL3_REGION=us-east-1
VESSEL3_DOMAIN=s3.myhomelab.net,s3.internal
VESSEL3_METRICS_TOKEN=metrics-secret-token-xyz
VESSEL3_COMPACT_THRESHOLD_BYTES=67108864
VESSEL3_LIFECYCLE_INTERVAL_SECONDS=3600
```

### Profile C: OIDC / SSO Integration (Keycloak / Authentik)
Delegates authentication to an OpenID Connect provider with JIT user provisioning:
```env
ASPNETCORE_URLS=http://0.0.0.0:9000
VESSEL3_DATA=/data
VESSEL3_ACCESS_KEY=V3AKADMINBOOTSTRAP01
VESSEL3_SECRET_KEY=bootstrap-secret-change-me
VESSEL3_OIDC_ISSUER=https://auth.example.com/realms/master
VESSEL3_OIDC_CLIENT_ID=vessel3-client
VESSEL3_OIDC_AUDIENCE=vessel3-client
VESSEL3_OIDC_REQUIRE_CLAIM=groups=storage-users
```

### Profile D: Docker Compose `.env`
```env
VESSEL3_DATA=/data
VESSEL3_ACCESS_KEY=AKIAIOSFODNN7EXAMPLE
VESSEL3_SECRET_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
VESSEL3_REGION=us-east-1
VESSEL3_DOMAIN=s3.local
```
Compose service excerpt:
```yaml
services:
  vessel3:
    image: ghcr.io/nechja/vessel3:latest-ui
    ports:
      - "9000:9000"
    env_file: .env
    volumes:
      - vessel3-data:/data

volumes:
  vessel3-data:
```

### Profile E: Kubernetes Pod `env` Snippet
```yaml
env:
  - name: ASPNETCORE_URLS
    value: "http://0.0.0.0:9000"
  - name: VESSEL3_DATA
    value: "/data"
  - name: VESSEL3_ACCESS_KEY
    valueFrom:
      secretKeyRef:
        name: vessel3-credentials
        key: access-key
  - name: VESSEL3_SECRET_KEY
    valueFrom:
      secretKeyRef:
        name: vessel3-credentials
        key: secret-key
  - name: VESSEL3_REGION
    value: "us-east-1"
  - name: VESSEL3_DOMAIN
    value: "s3.cluster.local"
```
*(Remember to configure `securityContext.fsGroup: 1654` on the Pod spec so the volume is writable by Vessel3).*
