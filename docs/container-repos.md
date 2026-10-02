# Container Repos (OCI Image Registry)

Vessel3 includes native support for Container Repos via the Open Container Initiative (OCI) Distribution Specification (Docker Registry v2 API). Container image layers, configs, and manifests share the same content-addressed immutable blob pool as S3 objects while maintaining separate protocol routing, authentication, and metadata tracking.

---

## Key Capabilities

- **OCI & Docker Registry v2 Compliant**: Fully compatible with `docker`, `podman`, `skopeo`, `oras`, `containerd`, and Kubernetes image pull secrets.
- **Unified Multi-Protocol Storage**: Container image layers and manifests are stored directly in Vessel3's deduplicating `IBlobPool`, sharing space and caching mechanisms with S3 buckets.
- **Cross-Protocol Garbage Collection**: The `GarbageCollector` queries both S3 buckets and Container Repos via `IBlobReferenceSource` before purging unreferenced blobs.
- **Flexible Authentication**:
  - **Docker Www-Authenticate Challenge**: Automatically issues Bearer JWT tokens via `/v2/token` from HTTP Basic Auth credentials.
  - **Vessel IAM Integration**: Uses standard Vessel3 access keys (`VESSEL3_ACCESS_KEY` / `VESSEL3_SECRET_KEY` or IAM service accounts).
  - **Direct Vessel Auth**: Internal tools and the Web UI can authenticate via `X-Vessel-Key` and `X-Vessel-Secret` headers.
- **Embedded Web UI**: Browse repositories, view tags, copy 1-click `docker pull` commands, and delete manifests directly from `/_ui`.

---

## Configuration

Container Repos are enabled by default and listen on the standard server port at `/v2/...`.

| Variable | Type | Default | Description |
|---|---|---|---|
| `VESSEL3_CONTAINER_REPOS_ENABLED` | Boolean | `true` | Enables or disables the OCI container repo endpoints. Also accepts `VESSEL3_OCI_ENABLED`. |
| `VESSEL3_DATA` | Path | `data` | The SQLite catalog database is stored under `<data>/oci/catalog.db`. |

---

## Using Standard Container Tools

### 1. Docker

Authenticate with your Vessel3 credentials:
```bash
docker login localhost:9000 -u <VESSEL3_ACCESS_KEY> -p <VESSEL3_SECRET_KEY>
```

Tag an image for your Vessel3 instance:
```bash
docker tag my-service:latest localhost:9000/my-service:v1.0.0
```

Push the image:
```bash
docker push localhost:9000/my-service:v1.0.0
```

Pull the image:
```bash
docker pull localhost:9000/my-service:v1.0.0
```

### 2. Podman

```bash
podman login localhost:9000 --tls-verify=false -u <VESSEL3_ACCESS_KEY> -p <VESSEL3_SECRET_KEY>
podman push --tls-verify=false my-service:latest localhost:9000/my-service:v1.0.0
podman pull --tls-verify=false localhost:9000/my-service:v1.0.0
```

### 3. ORAS (OCI Registry As Storage)

Push arbitrary files or OCI artifacts:
```bash
oras push localhost:9000/my-artifacts:v1.0.0 ./sample-artifact.tar.gz
oras pull localhost:9000/my-artifacts:v1.0.0
```

---

## Supported Endpoints

All endpoints are hosted under `/v2/...`:

| Method | Path | Description |
|---|---|---|
| `GET`, `HEAD` | `/v2/` | API version check (returns `Docker-Distribution-API-Version: registry/2.0`). |
| `GET` | `/v2/token` | Issues Bearer JWT token from Basic Auth or Anonymous request. |
| `GET` | `/v2/_catalog` | Lists container repos (`?n=<limit>&last=<last>`). |
| `GET` | `/v2/<repo>/tags/list` | Lists tags for a container repo (`?n=<limit>&last=<last>`). |
| `GET`, `HEAD` | `/v2/<repo>/blobs/<digest>` | Downloads or checks existence of layer or config blob. |
| `POST` | `/v2/<repo>/blobs/uploads/` | Starts chunked upload session or executes monolithic upload with `?digest=...`. |
| `PATCH` | `/v2/<repo>/blobs/uploads/<id>` | Appends chunk to upload session. |
| `PUT` | `/v2/<repo>/blobs/uploads/<id>?digest=...` | Commits chunked upload session and verifies sha256 digest. |
| `DELETE` | `/v2/<repo>/blobs/uploads/<id>` | Cancels in-flight upload session. |
| `GET`, `HEAD` | `/v2/<repo>/manifests/<ref>` | Fetches manifest by tag or digest. |
| `PUT` | `/v2/<repo>/manifests/<ref>` | Pushes manifest by tag or digest. |
| `DELETE` | `/v2/<repo>/manifests/<ref>` | Deletes manifest or tag. |

---

## C# SDK Usage

The `Vessel3.Client` package (`IVesselClient`) provides typed methods for managing container repos:

```csharp
using Vessel3.Client;

using var client = new VesselClient("http://127.0.0.1:9000", accessKey: "my-key", secretKey: "my-secret");

// List all container repos
var reposResult = await client.ListContainerReposAsync();
if (reposResult.TryGetValue(out var repos, out var err))
{
    foreach (var repo in repos)
    {
        Console.WriteLine($"Repo: {repo}");

        // List tags for this container repo
        var tagsResult = await client.ListContainerTagsAsync(repo);
        if (tagsResult.TryGetValue(out var tags, out _))
        {
            foreach (var tag in tags)
            {
                Console.WriteLine($"  - {tag}");
            }
        }
    }
}

// Delete a container manifest/tag
await client.DeleteContainerManifestAsync("my-service", "v1.0.0");
```
