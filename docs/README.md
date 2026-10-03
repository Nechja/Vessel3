# Vessel3 Documentation

Vessel3 is a single-binary, high-durability object server featuring full Amazon S3 wire compatibility, multi-user Identity & Access Management (IAM), capability-based bucket access policies, a Native REST API (`/v1/...`), a companion .NET Client SDK, and an embedded administrative Web UI.

---

## Documentation Index

| Guide | Description |
|---|---|
| [**S3 Protocol Support Matrix**](s3-protocol-support.md) | Detailed compatibility matrix of all supported Amazon S3 API operations, subresources, headers, and limitations. |
| [**S3 Client & Tooling Guide**](s3-api-and-tools.md) | Configuration and usage recipes for AWS CLI, MinIO `mc`, Python (`boto3`), and `rclone`. |
| [**Azure Blob Storage Guide**](azure-blob-storage.md) | Protocol compatibility matrix, Azurite emulation, and setup recipes for Azure SDKs, AzCopy, Azure CLI, and Storage Explorer. |
| [**IAM & Access Control**](iam-and-access-control.md) | Multi-user model, user roles (`Admin`, `Member`, `ReadOnly`), access key lifecycle, bucket ownership, and isolation rules. |
| [**Native REST API**](native-api.md) | Full endpoint specification for the `/v1/...` REST API covering IAM, buckets, objects, and maintenance sweeps. |
| [**C# .NET Client SDK**](client-sdk.md) | Guide and code recipes for the `Vessel3.Client` package (`IVesselClient` / `VesselClient`). |
| [**Container Repos (OCI Image Registry)**](container-repos.md) | OCI / Docker Registry v2 container repo distribution specification, auth, and usage. |
| [**WebDAV Protocol & Network Drives**](webdav.md) | RFC 4918 WebDAV support, Class 1 & 2 operations, and mounting recipes for Windows, macOS, Linux, and mobile. |
| [**Environment Variables Cheat Sheet**](env-cheat-sheet.md) | Complete environment variable reference table, category breakdown, and copy-paste `.env` templates. |
| [**Deployment & Operations**](deployment-and-operations.md) | Docker, Kubernetes, systemd, Caddy reverse proxy, storage engine architecture, compaction, and GC. |
| [**OIDC & Single Sign-On**](oidc-and-sso.md) | Identity federation via OIDC, STS `AssumeRoleWithWebIdentity`, JIT user provisioning, and Web UI PKCE login. |

---

## System Architecture

- **Client Interfaces**: Standard S3 SDKs, AWS CLI, MinIO `mc`, Python `boto3`, Azure SDKs, AzCopy, Azure CLI, Azure Storage Explorer, Docker, Podman, ORAS, HTTP clients, and web browsers.
- **Protocol Dispatch**: Multi-protocol support on a single port:
  - **S3 Wire Protocol**: SigV4 authentication, STS web identity, and standard XML serialization.
  - **Azure Blob Storage Wire Protocol**: SharedKey / SharedKeyLite HMAC-SHA256 authentication, Block Blobs, staged blocks (`comp=block`/`comp=blocklist`), and Azurite emulation.
  - **Native REST API**: `/v1/...` with Bearer JWT or `Vessel` token authentication and JSON serialization.
  - **Container Repos (OCI)**: `/v2/...` OCI / Docker Registry v2 with Bearer token authentication and JSON manifests.
  - **WebDAV Wire Protocol**: `/dav/...` and `/webdav/...` RFC 4918 Class 1 & 2 support with HTTP Basic Authentication and Multi-Status XML.
- **Middleware Pipeline**: Request telemetry, Prometheus metrics (`/metrics`), virtual-host domain resolution, CORS, and auth dispatch.
- **Core Storage Layer**:
  - `BucketRegistry` & `BucketPolicy`: Bucket metadata, ownership tracking, and capability authorization.
  - `IdentityRegistry`: Local SQLite user and access key store (`iam.db`).
  - `ObjectStore` & `BlobPool`: Content-addressed immutable blob pool (`aa/bb/<sha256>`) with atomic fsync writes.
  - Background Workers: Automatic catalog compaction, lifecycle expiration sweeps, and blob garbage collection.
- **Embedded Web UI**: Single-page Blazor WebAssembly management console served at `/_ui`.

---

## Quick Reference

- **Default listen address**: `http://127.0.0.1:9000` (configurable via `ASPNETCORE_URLS` or `--urls`)
- **Web UI endpoint**: `http://127.0.0.1:9000/_ui` (or `admin.<domain>` with virtual host routing)
- **Prometheus Metrics**: `http://127.0.0.1:9000/metrics`
- **Default Data Root**: `./data` (configurable via `VESSEL3_DATA`)
