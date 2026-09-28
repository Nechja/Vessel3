# S3 Client & Tooling Guide

Vessel3 works out-of-the-box with standard Amazon S3 tools, CLI utilities, and SDKs.

---

## 1. AWS CLI

### Configuration

Add a profile to `~/.aws/credentials`:
```ini
[vessel3]
aws_access_key_id = V3AKADMINEXAMPLE1234
aws_secret_access_key = your-secret-key-token
```

Set region in `~/.aws/config`:
```ini
[profile vessel3]
region = us-east-1
output = json
```

### Usage Examples

```sh
export ENDPOINT=http://127.0.0.1:9000

# 1. Create a bucket
aws --profile vessel3 --endpoint-url $ENDPOINT s3 mb s3://backups

# 2. Upload and download objects
aws --profile vessel3 --endpoint-url $ENDPOINT s3 cp ./database.dump s3://backups/
aws --profile vessel3 --endpoint-url $ENDPOINT s3 cp s3://backups/database.dump ./restored.dump

# 3. Enable bucket versioning
aws --profile vessel3 --endpoint-url $ENDPOINT s3api put-bucket-versioning \
  --bucket backups \
  --versioning-configuration Status=Enabled

# 4. Set public-read ACL
aws --profile vessel3 --endpoint-url $ENDPOINT s3api put-bucket-acl \
  --bucket backups \
  --acl public-read

# 5. Configure static website hosting
aws --profile vessel3 --endpoint-url $ENDPOINT s3 website s3://backups \
  --index-document index.html \
  --error-document 404.html

# 6. Apply lifecycle retention policy
aws --profile vessel3 --endpoint-url $ENDPOINT s3api put-bucket-lifecycle-configuration \
  --bucket backups \
  --lifecycle-configuration '{
    "Rules": [
      {
        "ID": "PruneOldVersions",
        "Status": "Enabled",
        "Prefix": "",
        "NoncurrentVersionExpiration": { "NoncurrentDays": 30 },
        "Expiration": { "ExpiredObjectDeleteMarker": true }
      }
    ]
  }'
```

---

## 2. MinIO Client (`mc`)

### Configuration

```sh
mc alias set vessel3 http://127.0.0.1:9000 V3AKADMINEXAMPLE1234 your-secret-key-token
```

### Usage Examples

```sh
# List buckets
mc ls vessel3/

# Create a bucket
mc mb vessel3/photos

# Mirror / sync directory to bucket
mc mirror ./photos vessel3/photos/

# Inspect bucket metadata and stats
mc stat vessel3/photos
```

---

## 3. Python (`boto3`)

```python
import boto3

s3 = boto3.client(
    "s3",
    endpoint_url="http://127.0.0.1:9000",
    aws_access_key_id="V3AKADMINEXAMPLE1234",
    aws_secret_access_key="your-secret-key-token",
    region_name="us-east-1",
)

# Create bucket
s3.create_bucket(Bucket="analytics")

# Upload file with metadata
with open("report.csv", "rb") as f:
    s3.put_object(
        Bucket="analytics",
        Key="2026/report.csv",
        Body=f,
        ContentType="text/csv",
        Metadata={"Quarter": "Q3", "Source": "IngestPipeline"},
    )

# Download file
response = s3.get_object(Bucket="analytics", Key="2026/report.csv")
content = response["Body"].read()

# Generate presigned download URL (valid for 1 hour)
url = s3.generate_presigned_url(
    "get_object",
    Params={"Bucket": "analytics", "Key": "2026/report.csv"},
    ExpiresIn=3600,
)
print("Presigned URL:", url)
```

---

## 4. `rclone`

Configure `~/.config/rclone/rclone.conf`:
```ini
[vessel3]
type = s3
provider = Other
env_auth = false
access_key_id = V3AKADMINEXAMPLE1234
secret_access_key = your-secret-key-token
endpoint = http://127.0.0.1:9000
region = us-east-1
acl = private
```

Sync commands:
```sh
# Sync local directory to Vessel3 bucket
rclone sync /var/data vessel3:archive/

# List remote files
rclone ls vessel3:archive/
```

---

## Path-Style vs. Virtual-Host Style Routing

### Path-Style (Default)
When `VESSEL3_DOMAIN` is unset:
- Bucket URI: `http://127.0.0.1:9000/<bucket>/<key>`
- All S3 clients must configure path-style addressing (`--force-path-style` or `s3_use_path_style = true`).

### Virtual-Host Routing
When `VESSEL3_DOMAIN=s3.example.com` is configured:
- Bucket URI: `http://<bucket>.s3.example.com/<key>`
- Requests with `Host: admin.s3.example.com` automatically redirect to the Web UI (`/_ui`).
