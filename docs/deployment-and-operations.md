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
