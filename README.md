# Vessel3

[![probe](https://github.com/Nechja/Vessel3/actions/workflows/probe.yml/badge.svg)](https://github.com/Nechja/Vessel3/actions/workflows/probe.yml)
[![container](https://github.com/Nechja/Vessel3/actions/workflows/container.yml/badge.svg)](https://github.com/Nechja/Vessel3/actions/workflows/container.yml)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache_2.0-blue.svg)](LICENSE)

A single-binary object server built for homelabs and software testing.

I don't have a roadmap yet, but I'm working toward an all-in-one storage system for homelabs.

---

## What it is

- Self-hosted and object storage for a homelab or test setup: one place for your files and the data your applications need to store.
- Compatible with common S3 and Azure storage clients
- WebDAV clients that can use it like a network drive
- Includes a browser interface (optional) 
- A place to store container images.
- Designed to keep writes consistent even during unexpected server problems.
- dotnet 10 because why not

## What it isn't

- It isn't managed for you. You run it, and you're responsible for the machine, its storage, and keeping your data backed up.
- It doesn't keep a second copy of your data on another server or automatically stay available if this one fails (yet).
- It isn't a full replacement for a network-attached storage system
- It can serve a simple website, but it isn't meant to do everything those systems do.
- It isn't built for large teams or heavy, high-traffic workloads. It's a personal project for homelabs and testing, and it's still growing.
- The best code ever written

---

## Documentation

Full documentation is available in the [`docs/`](docs/README.md) directory:

| Guide | Description |
|---|---|
| [**S3 Protocol Support Matrix**](docs/s3-protocol-support.md) | Full compatibility matrix of all supported Amazon S3 API operations, subresources, and headers. |
| [**S3 Client & Tooling Guide**](docs/s3-api-and-tools.md) | Setup and usage examples for AWS CLI, MinIO `mc`, Python `boto3`, and `rclone`. |
| [**Azure Blob Storage Guide**](docs/azure-blob-storage.md) | Compatibility matrix, Azurite emulation, and setup recipes for Azure SDKs, AzCopy, Azure CLI, and Storage Explorer. |
| [**IAM & Access Control**](docs/iam-and-access-control.md) | Multi-user identity model, roles, access key management, bucket ownership, and policy evaluation. |
| [**Native REST API**](docs/native-api.md) | Specification for the `/v1/...` REST API for IAM, buckets, objects, and administrative sweeps. |
| [**C# .NET Client SDK**](docs/client-sdk.md) | Guide and code recipes for the `Vessel3.Client` package (`IVesselClient`). |
| [**Container Repos (OCI Registry)**](docs/container-repos.md) | OCI / Docker Registry v2 container repo distribution specification, auth, and usage recipes. |
| [**WebDAV Protocol & Network Drives**](docs/webdav.md) | RFC 4918 WebDAV support, Class 1 & 2 operations, and mounting recipes for Windows, macOS, Linux, and mobile. |
| [**Environment Variables Cheat Sheet**](docs/env-cheat-sheet.md) | Complete environment variable reference, category breakdown, and copy-paste `.env` profiles. |
| [**Deployment & Operations**](docs/deployment-and-operations.md) | Docker, Kubernetes, systemd service, reverse proxy setup (Caddy), compaction, and GC. |
| [**OIDC & Single Sign-On**](docs/oidc-and-sso.md) | OpenID Connect federation, STS AssumeRoleWithWebIdentity, JIT provisioning, and Web UI PKCE. |

---

## Quickstart

### 1. Run via Docker

```sh
docker run -d \
  --name vessel3 \
  -p 9000:9000 \
  -e VESSEL3_ACCESS_KEY=AKIAIOSFODNN7EXAMPLE \
  -e VESSEL3_SECRET_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY \
  -e VESSEL3_DATA=/data \
  -v vessel3-data:/data \
  ghcr.io/nechja/vessel3:latest-ui
```

Open `http://127.0.0.1:9000/_ui` in your browser to access the Web UI.

### 2. Connect via AWS CLI

```sh
export AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
export AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
export ENDPOINT=http://127.0.0.1:9000

# Create a bucket
aws --endpoint-url $ENDPOINT s3 mb s3://my-bucket

# Upload a file
aws --endpoint-url $ENDPOINT s3 cp ./photo.jpg s3://my-bucket/

# List files
aws --endpoint-url $ENDPOINT s3 ls s3://my-bucket/
```

### 3. Connect via C# SDK

```csharp
using Vessel3.Client;

using var client = new VesselClient(
    baseUrl: "http://127.0.0.1:9000",
    accessKey: "AKIAIOSFODNN7EXAMPLE",
    secretKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

// List buckets
var bucketsResult = await client.ListBucketsAsync();
```

---

## Configuration

All configuration is provided via environment variables. See the [**Environment Variables Cheat Sheet**](docs/env-cheat-sheet.md) for full descriptions and deployment templates.

| Variable | Default | Meaning |
|---|---|---|
| `VESSEL3_DATA` | `data` next to binary | Data root (blobs, buckets, index, log, IAM). Persist this path. |
| `VESSEL3_ACCESS_KEY` | *unset* (auth disabled) | Root SigV4 access key ID. Automatically provisions bootstrap `admin` user. |
| `VESSEL3_SECRET_KEY` | *unset* (auth disabled) | Root SigV4 secret key. |
| `VESSEL3_REGION` | `us-east-1` | Region string for SigV4 and location constraints. |
| `VESSEL3_DOMAIN` | *unset* (path-style only) | Base domains for virtual-host routing (`s3.example.com,localhost`). `admin.<domain>` routes to the Web UI. |
| `VESSEL3_METRICS_TOKEN` | *unset* | Bearer token required for `/metrics` when queried from non-loopback IPs. |
| `VESSEL3_METRICS_ALLOW_ANONYMOUS` | `false` | When `true`, `/metrics` is public. |
| `VESSEL3_LIFECYCLE_INTERVAL_SECONDS` | `3600` | Background lifecycle sweep frequency in seconds (`0` disables). |
| `VESSEL3_COMPACT_INTERVAL_SECONDS` | `3600` | Background compaction sweep frequency in seconds (`0` disables). |
| `VESSEL3_COMPACT_THRESHOLD_BYTES` | `67108864` (64 MiB) | Event log size threshold before compaction runs. |
| `VESSEL3_GC_MAX_WAIT_SECONDS` | `120` | Max wait time for in-flight writes before aborting Blob GC sweep. |
| `VESSEL3_SLOW_REQUEST_MS` | `1000` | Request duration threshold in ms to trigger slow request logging. |
| `VESSEL3_OIDC_ISSUER` | *unset* | OpenID Connect discovery URL. |
| `VESSEL3_OIDC_CLIENT_ID` | *unset* | Client ID for OIDC tokens. Required with issuer. |
| `VESSEL3_OIDC_AUDIENCE` | *unset* | Optional secondary audience accepted in tokens. |
| `VESSEL3_OIDC_REQUIRE_CLAIM` | *unset* | `name=value` claim assertion required on incoming tokens. |
| `ASPNETCORE_URLS` | `http://127.0.0.1:9000` | Kestrel listen URLs. Can also be set via `--urls`. |

---

## Storage Layout

```
VESSEL3_DATA/
  blobs/
    aa/bb/<sha256>              Content-addressed immutable object bytes
    tmp/<guid>                  In-flight writes prior to atomic rename
  buckets/<bucket-name>/
    log                         Append-only event log (source of truth)
    index.db                    SQLite metadata catalog (WAL mode)
    snapshot.db                 Catalog snapshot written by compaction
    versioning                  Bucket versioning state
    object-lock.json            Bucket object lock configuration
    lifecycle.json              Bucket lifecycle rules
    website.json                Bucket static website configuration
    cors.json                   Bucket CORS configuration
    access.json                 Bucket access policy flags
  iam/
    iam.db                      SQLite multi-user & access key database
  uploads/<upload-id>/          In-flight multipart parts
```

---

## Durability & Maintenance

- **Crash Consistency**: Every object write streams to disk, fsyncs, and commits via atomic rename and append-only event log fsync. A reader querying a key during a concurrent overwrite sees either the old or new version, never an incomplete write or missing key.
- **Compaction**: `PUT /_admin/compact` (or automatic background interval) flushes SQLite catalog state into `snapshot.db` and truncates the event log, keeping write logs bounded on high-churn workloads.
- **Blob Garbage Collection**: `PUT /_admin/gc` (or `POST /v1/admin/gc`) reclaims unreferenced blobs and orphan multipart uploads while respecting active writers and Object Lock retention.
- **Lifecycle Sweeper**: `PUT /_admin/lifecycle` (or `POST /v1/admin/sweep`) applies expiration rules and prunes noncurrent versions and delete markers.

---

## Building from Source

Requires .NET 10 SDK:

```sh
# Build server binary
dotnet publish Vessel3.Server -c Release -r linux-x64 --self-contained
```

---

## License

Apache 2.0. See [LICENSE](LICENSE).

---

## AI Use

AI's did design a lot/most of the testing in this repo, and assist with the readme.
