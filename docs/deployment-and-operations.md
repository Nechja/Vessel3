# Deployment & Operations Guide

Vessel3 ships as a single native binary or lightweight container image. This guide covers production deployment patterns, reverse proxy setups, storage durability semantics, and maintenance operations.

---

## 1. Container Deployment

Two image variants are published per release:
- `ghcr.io/nechja/vessel3:latest`: Pure S3 and Native REST API server.
- `ghcr.io/nechja/vessel3:latest-ui`: Embeds the Blazor Web UI at `/_ui`.

### Docker Run

```sh
docker run -d \
  --name vessel3 \
  -p 9000:9000 \
  -e VESSEL3_ACCESS_KEY=V3AKADMINEXAMPLE1234 \
  -e VESSEL3_SECRET_KEY=9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b \
  -e VESSEL3_DATA=/data \
  -v vessel3-storage:/data \
  ghcr.io/nechja/vessel3:latest-ui
```

### Docker Compose

```yaml
services:
  vessel3:
    image: ghcr.io/nechja/vessel3:latest-ui
    container_name: vessel3
    restart: unless-stopped
    ports:
      - "9000:9000"
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:9000
      - VESSEL3_DATA=/data
      - VESSEL3_ACCESS_KEY=V3AKADMINEXAMPLE1234
      - VESSEL3_SECRET_KEY=9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b
      - VESSEL3_REGION=us-east-1
      - VESSEL3_DOMAIN=s3.local
    volumes:
      - vessel3-data:/data

volumes:
  vessel3-data:
```

### Kubernetes

Vessel3 runs as a non-root user (`uid 1654`). Ensure `securityContext.fsGroup: 1654` is set so the volume mount is writable:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: vessel3
  namespace: storage
spec:
  replicas: 1
  strategy:
    type: Recreate
  selector:
    matchLabels:
      app: vessel3
  template:
    metadata:
      labels:
        app: vessel3
    spec:
      securityContext:
        fsGroup: 1654
      containers:
        - name: vessel3
          image: ghcr.io/nechja/vessel3:latest-ui
          ports:
            - containerPort: 9000
          env:
            - name: ASPNETCORE_URLS
              value: "http://0.0.0.0:9000"
            - name: VESSEL3_DATA
              value: "/data"
            - name: VESSEL3_ACCESS_KEY
              valueFrom:
                secretKeyRef:
                  name: vessel3-creds
                  key: access-key
            - name: VESSEL3_SECRET_KEY
              valueFrom:
                secretKeyRef:
                  name: vessel3-creds
                  key: secret-key
          volumeMounts:
            - name: data
              mountPath: /data
      volumes:
        - name: data
          persistentVolumeClaim:
            claimName: vessel3-pvc
```

---

## 2. Linux Standalone Binary & Systemd

Download release binary:
```sh
curl -L https://github.com/Nechja/Vessel3/releases/latest/download/vessel3-v0.6.0-linux-x64.tar.gz | tar -xz -C /usr/local/bin
useradd -r -s /bin/false -u 1654 vessel3
mkdir -p /var/lib/vessel3
chown -R vessel3:vessel3 /var/lib/vessel3
```

Systemd service unit (`/etc/systemd/system/vessel3.service`):
```ini
[Unit]
Description=Vessel3 S3 Object Server
After=network.target

[Service]
Type=simple
User=vessel3
Group=vessel3
WorkingDirectory=/var/lib/vessel3
Environment="ASPNETCORE_URLS=http://127.0.0.1:9000"
Environment="VESSEL3_DATA=/var/lib/vessel3"
Environment="VESSEL3_ACCESS_KEY=V3AKADMINEXAMPLE1234"
Environment="VESSEL3_SECRET_KEY=9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b"
Environment="VESSEL3_DOMAIN=s3.example.com"
ExecStart=/usr/local/bin/vessel3
Restart=always
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

Enable and start:
```sh
systemctl daemon-reload
systemctl enable --now vessel3
```

---

## 3. Reverse Proxy Configuration (Caddy)

```caddyfile
# Wildcard S3 domain + admin subdomain
*.s3.example.com, s3.example.com {
    tls internal # Or your Let's Encrypt credentials

    # Max body upload 5GB
    request_body {
        max_size 5GB
    }

    reverse_proxy 127.0.0.1:9000 {
        # Preserve Host header for virtual-host routing
        header_up Host {host}
        header_up X-Forwarded-Proto {scheme}
    }
}
```

---

## 4. Storage Engine & Durability

Vessel3 guarantees crash-safe durability without distributed consensus:

1. **Content-Addressed Blobs**:
   - Every incoming `PUT` streams into a temporary file under `blobs/tmp/`.
   - File is `fsync`'d to disk.
   - File is atomically renamed to its content-addressed SHA-256 location (`blobs/aa/bb/<sha256>`).
2. **Append-Only Write Log (`log`)**:
   - Bucket state mutations are serialized into per-bucket append-only logs.
   - Every record is `fsync`'d before acknowledging the HTTP request.
3. **SQLite WAL Catalog (`index.db`)**:
   - Object lookups, versioning, and prefixes run out of a SQLite database in Write-Ahead Log (`WAL`) mode with `NORMAL` synchronous pragma.
   - If `index.db` is deleted or corrupted, Vessel3 automatically replays the event log from the last checkpoint on next startup.

---

## 5. Maintenance Operations

### Compaction (`PUT /_admin/compact`)
- Checkpoints the SQLite index into `snapshot.db` via atomic rename and truncates the event `log`.
- Runs automatically in the background (frequency controlled by `VESSEL3_COMPACT_INTERVAL_SECONDS`, threshold by `VESSEL3_COMPACT_THRESHOLD_BYTES`).
- Manual trigger:
  ```sh
  curl -X PUT "http://127.0.0.1:9000/_admin/compact?min-bytes=0"
  ```

### Blob Garbage Collection (`PUT /_admin/gc` or `POST /v1/admin/gc`)
- Sweeps unreferenced blobs and abandoned multipart uploads.
- Respects active in-flight writes and Object Lock retention.
- Manual trigger:
  ```sh
  # S3 Admin endpoint (loopback)
  curl -X PUT "http://127.0.0.1:9000/_admin/gc?blob-age=3600&upload-age=604800"

  # Native API (with credentials)
  curl -X POST "http://127.0.0.1:9000/v1/admin/gc?minBlobAgeSec=3600&minUploadAgeSec=604800" \
    -H "Authorization: Vessel V3AKADMINEXAMPLE1234:secret"
  ```

### Lifecycle Sweeper (`PUT /_admin/lifecycle` or `POST /v1/admin/sweep`)
- Evaluates bucket lifecycle expiration rules and prunes noncurrent versions and delete markers.
- Background frequency configured via `VESSEL3_LIFECYCLE_INTERVAL_SECONDS`.
- Manual trigger:
  ```sh
  # S3 Admin endpoint (loopback)
  curl -X PUT "http://127.0.0.1:9000/_admin/lifecycle"

  # Native API (with credentials)
  curl -X POST "http://127.0.0.1:9000/v1/admin/sweep" \
    -H "Authorization: Vessel V3AKADMINEXAMPLE1234:secret"
  ```

---

## 6. Telemetry & Monitoring

### 6.1 Prometheus Metrics

Vessel3 exports Prometheus metrics at:
```http
GET /metrics
```

Key metric families:
- `vessel3_requests_total`: Total request counter by method, path, and HTTP status.
- `vessel3_request_duration_seconds`: Request duration histogram.
- `vessel3_bytes_received_total` & `vessel3_bytes_sent_total`: Network throughput.
- `vessel3_bucket_bytes`: Current byte size per bucket.
- `vessel3_bucket_objects`: Object count per bucket.

Access to `/metrics` from non-loopback addresses requires `Authorization: Bearer <VESSEL3_METRICS_TOKEN>` unless `VESSEL3_METRICS_ALLOW_ANONYMOUS=true` is set.

---

### 6.2 OpenTelemetry Distributed Tracing

Vessel3 provides native distributed tracing via .NET `ActivitySource("Vessel3")` with zero third-party agent dependencies, full W3C Trace Context propagation, and native OTLP/HTTP export.

#### Configuration

| Environment Variable | Default | Purpose |
|---|---|---|
| `VESSEL3_OTEL_ENABLED` | `false` | Enables native distributed tracing activities and `traceparent` propagation. |
| `VESSEL3_OTEL_EXPORTER_OTLP_ENDPOINT` | *unset* | OTLP HTTP trace collector endpoint (e.g., `http://otel-collector:4318/v1/traces`). Automatically enables OTel when set. |
| `VESSEL3_OTEL_SERVICE_NAME` | `vessel3` | Logical service name emitted in OpenTelemetry resource attributes. |

#### W3C Trace Context Propagation

- **Inbound Context**: If an incoming request includes a W3C `traceparent` header (e.g., `00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01`), Vessel3 parses it and binds the server activity as a child span of the caller's trace context.
- **Outbound Context**: Every response includes the active `traceparent` header, allowing callers and downstream proxies to correlate requests end-to-end.

#### Span Attributes & Multi-Protocol Semantic Conventions

Every request span is enriched with contextual attributes:
- `rpc.system`: Protocol classifier (`s3`, `azure-blob`, `native`, `container-registry`, `webdav`, `metrics`, `admin`, `web-ui`).
- `http.request.method`: HTTP method (`GET`, `PUT`, `DELETE`, etc.).
- `vessel3.protocol`: Protocol engine handling the request.
- `vessel3.action`: Protocol-specific operation (e.g., `PutObject`, `GetObject`, `ListBlobs`, `StreamEvents`).
- `vessel3.bucket`: Target bucket or repository name (when applicable).
- `vessel3.key`: Target object key or blob name (when applicable).
- `vessel3.actor`: Authenticated identity (access key, username, or client ID).
- `http.response.status_code`: Final HTTP status code.

In the event of an unhandled exception or 5xx response, the activity status is automatically flagged as `ActivityStatusCode.Error` with the exception description recorded on the span.

#### OpenTelemetry Collector & Jaeger Integration

```yaml
services:
  vessel3:
    image: ghcr.io/nechja/vessel3:latest-ui
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:9000
      - VESSEL3_DATA=/data
      - VESSEL3_ACCESS_KEY=V3AKADMINEXAMPLE1234
      - VESSEL3_SECRET_KEY=9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b
      - VESSEL3_OTEL_ENABLED=true
      - VESSEL3_OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318/v1/traces
      - VESSEL3_OTEL_SERVICE_NAME=vessel3-storage
    ports:
      - "9000:9000"
    volumes:
      - vessel3-data:/data

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: ["--config=/etc/otel-collector-config.yaml"]
    volumes:
      - ./otel-collector-config.yaml:/etc/otel-collector-config.yaml
    ports:
      - "4318:4318" # OTLP HTTP receiver

  jaeger:
    image: jaegertracing/all-in-one:latest
    ports:
      - "16686:16686" # Jaeger UI
```

Collector configuration (`otel-collector-config.yaml`):
```yaml
receivers:
  otlp:
    protocols:
      http:
        endpoint: 0.0.0.0:4318

exporters:
  otlp/jaeger:
    endpoint: jaeger:4317
    tls:
      insecure: true

service:
  pipelines:
    traces:
      receivers: [otlp]
      exporters: [otlp/jaeger]
```

---

## 7. Kubernetes Operator Management

The Vessel3 Kubernetes Operator (`vessel3-operator`) provides declarative GitOps lifecycle management for Vessel3 clusters via Custom Resource Definitions under the `vessel.nechja.io` group.

### Custom Resource Definitions (CRDs)

| CRD | Kind | Description |
|---|---|---|
| `vesselservers.vessel.nechja.io` | `VesselServer` | Manages Vessel3 server deployments, persistent storage claims, service bindings, and credentials. |
| `vesselbuckets.vessel.nechja.io` | `VesselBucket` | Declaratively manages bucket creation, versioning state, and access policies. |
| `vesselusers.vessel.nechja.io` | `VesselUser` | Manages IAM identities, role assignments (`Admin`, `Member`, `ReadOnly`), and access keys. |
| `vesselwebhooks.vessel.nechja.io` | `VesselWebhook` | Declaratively manages webhook endpoints, secret bindings, event subscriptions, and resource filters. |

### Installing via Helm

```sh
# Install CRDs
kubectl apply -f deploy/crds/

# Install Operator via Helm
helm install vessel3-operator ./charts/vessel3-operator \
  --namespace vessel3-system \
  --create-namespace
```

### Declarative Webhook Example

```yaml
apiVersion: vessel.nechja.io/v1alpha1
kind: VesselWebhook
metadata:
  name: container-events
  namespace: storage
spec:
  serverRef:
    name: vessel-primary
  name: "tekton-pipeline-trigger"
  url: "http://el-tekton-listener.ci.svc:8080"
  secretRef:
    secretName: "webhook-auth"
    key: "token"
  eventFilters:
    - "container.image.pushed"
    - "s3.object.created"
  resourceFilters:
    - "production/*"
  active: true
```

