# S3 Protocol Support & Compatibility Matrix

Vessel3 implements the Amazon S3 wire protocol over HTTP/1.1 and HTTP/2. Existing S3 SDKs, command-line utilities, and backup tools interact with Vessel3 directly without modifications.

---

## Service Operations

| API Operation | HTTP Method & URI | Support Status | Notes |
|---|---|---|---|
| `ListBuckets` | `GET /` | **Full** | Returns `ListAllMyBucketsResult` XML. Scoped by caller: non-admin callers only see buckets they own; admins see all buckets. |

---

## Bucket Operations

| API Operation | HTTP Method & Subresource | Support Status | Notes |
|---|---|---|---|
| `CreateBucket` | `PUT /{bucket}` | **Full** | Creates bucket. Caller becomes bucket owner. Denied to `ReadOnly` users. |
| `DeleteBucket` | `DELETE /{bucket}` | **Full** | Deletes empty bucket. Requires `Admin` capability (bucket owner or admin user). |
| `HeadBucket` | `HEAD /{bucket}` | **Full** | Returns `200 OK` if bucket exists and caller has read access, `404 Not Found` if missing, or `403 Forbidden`. |
| `GetBucketLocation` | `GET /{bucket}?location` | **Full** | Returns `LocationConstraint` XML (`VESSEL3_REGION`, default `us-east-1`). |
| `ListObjects` (v1) | `GET /{bucket}` | **Full** | Supports `prefix`, `delimiter`, `marker`, `max-keys`, and `encoding-type=url`. |
| `ListObjectsV2` | `GET /{bucket}?list-type=2` | **Full** | Supports `prefix`, `delimiter`, `continuation-token`, `start-after`, `max-keys`, `fetch-owner`, and `encoding-type=url`. |
| `ListObjectVersions` | `GET /{bucket}?versions` | **Full** | Returns versions and delete markers. Supports `prefix`, `delimiter`, `key-marker`, `version-id-marker`, `max-keys`, `encoding-type`. |
| `GetBucketVersioning` | `GET /{bucket}?versioning` | **Full** | Returns XML `VersioningConfiguration` (`Status`: `Enabled` or `Suspended`). |
| `PutBucketVersioning` | `PUT /{bucket}?versioning` | **Full** | Sets bucket versioning to `Enabled` or `Suspended`. |
| `GetBucketAcl` | `GET /{bucket}?acl` | **Full** | Returns `AccessControlPolicy` XML with bucket owner ID. |
| `PutBucketAcl` | `PUT /{bucket}?acl` | **Semantics** | Canned ACLs `public-read` and `private` are supported via `x-amz-acl` header or XML body with `AllUsers` grant. Maps to bucket capability policy. |
| `GetBucketCors` | `GET /{bucket}?cors` | **Full** | Returns XML `CORSConfiguration`. |
| `PutBucketCors` | `PUT /{bucket}?cors` | **Full** | Configures CORS rules (`AllowedOrigin`, `AllowedMethod`, `AllowedHeader`, `ExposeHeader`, `MaxAgeSeconds`). |
| `DeleteBucketCors` | `DELETE /{bucket}?cors` | **Full** | Removes bucket CORS configuration. |
| `GetBucketLifecycle` | `GET /{bucket}?lifecycle` | **Full** | Returns XML `LifecycleConfiguration`. |
| `PutBucketLifecycle` | `PUT /{bucket}?lifecycle` | **Full** | Supports `Expiration` (`Days`), `NoncurrentVersionExpiration` (`NoncurrentDays`), and `ExpiredObjectDeleteMarker`. |
| `DeleteBucketLifecycle` | `DELETE /{bucket}?lifecycle` | **Full** | Deletes bucket lifecycle configuration. |
| `GetBucketWebsite` | `GET /{bucket}?website` | **Full** | Returns XML `WebsiteConfiguration`. |
| `PutBucketWebsite` | `PUT /{bucket}?website` | **Full** | Sets `IndexDocument` (`Suffix`) and `ErrorDocument` (`Key`). |
| `DeleteBucketWebsite` | `DELETE /{bucket}?website` | **Full** | Removes static website configuration. |
| `GetObjectLockConfig` | `GET /{bucket}?object-lock` | **Full** | Returns XML `ObjectLockConfiguration` (default retention mode and days/years). |
| `PutObjectLockConfig` | `PUT /{bucket}?object-lock` | **Full** | Configures default Object Lock on bucket. |
| `ListMultipartUploads` | `GET /{bucket}?uploads` | **Full** | Lists active in-flight multipart uploads. |
| `DeleteObjects` | `POST /{bucket}?delete` | **Full** | Multi-object batch delete. Atomic single-log write with single fsync. Supports `Quiet` mode. |

---

## Object Operations

| API Operation | HTTP Method & Subresource | Support Status | Notes |
|---|---|---|---|
| `GetObject` | `GET /{bucket}/{key}` | **Full** | Streams bytes from content-addressed pool. Supports `versionId` query, HTTP range requests, and conditional headers. |
| `HeadObject` | `HEAD /{bucket}/{key}` | **Full** | Returns object metadata, `ETag`, `Content-Length`, `Content-Type`, `Last-Modified`, and `x-amz-version-id`. |
| `PutObject` | `PUT /{bucket}/{key}` | **Full** | Streams body to temp file, fsyncs, and commits via atomic rename and append-only log record. Supports metadata, checksums, tags, and object lock retention. |
| `CopyObject` | `PUT /{bucket}/{key}` | **Full** | Server-side copy triggered by `x-amz-copy-source` header. Avoids re-uploading bytes when blob already exists. |
| `DeleteObject` | `DELETE /{bucket}/{key}` | **Full** | Versioned bucket creates `DeleteMarker`. Specific version deleted via `?versionId={id}`. Supports `x-amz-bypass-governance-retention`. |
| `GetObjectAttributes` | `GET /{bucket}/{key}?attributes` | **Full** | Returns `ETag`, `Checksum`, `ObjectParts`, `StorageClass`, and `ObjectSize`. |
| `GetObjectTagging` | `GET /{bucket}/{key}?tagging` | **Full** | Returns XML `Tagging` (up to 10 tags per object). Supports `versionId`. |
| `PutObjectTagging` | `PUT /{bucket}/{key}?tagging` | **Full** | Sets object tags. Supports `versionId`. |
| `DeleteObjectTagging` | `DELETE /{bucket}/{key}?tagging` | **Full** | Clears tags from object version. |
| `GetObjectRetention` | `GET /{bucket}/{key}?retention` | **Full** | Returns Object Lock retention (`Mode`: `GOVERNANCE` / `COMPLIANCE`, `RetainUntilDate`). |
| `PutObjectRetention` | `PUT /{bucket}/{key}?retention` | **Full** | Sets per-object retention. Requires `versionId`. |
| `GetObjectLegalHold` | `GET /{bucket}/{key}?legal-hold` | **Full** | Returns legal hold status (`Status`: `ON` / `OFF`). |
| `PutObjectLegalHold` | `PUT /{bucket}/{key}?legal-hold` | **Full** | Sets legal hold status (`ON` / `OFF`). |

---

## Multipart Upload Operations

| API Operation | HTTP Method & Query | Support Status | Notes |
|---|---|---|---|
| `CreateMultipartUpload` | `POST /{bucket}/{key}?uploads` | **Full** | Allocates upload ID and stager directory under `uploads/<uploadId>`. |
| `UploadPart` | `PUT /{bucket}/{key}?uploadId={id}&partNumber={n}` | **Full** | Staged part must be >= 5 MiB (except last part). Max part size: 5 GiB. |
| `UploadPartCopy` | `PUT /{bucket}/{key}?uploadId={id}&partNumber={n}` | **Full** | Copies byte range from existing object into multipart part via `x-amz-copy-source`. |
| `ListParts` | `GET /{bucket}/{key}?uploadId={id}` | **Full** | Returns XML `ListPartsResult` with part numbers, sizes, and ETags. |
| `CompleteMultipartUpload` | `POST /{bucket}/{key}?uploadId={id}` | **Full** | Assembles parts into final content-addressed blob. Computes composite ETag (`<hash>-<parts>`). |
| `AbortMultipartUpload` | `DELETE /{bucket}/{key}?uploadId={id}` | **Full** | Removes staged part files and cleans up staging directory. |

---

## Security Token Service (STS)

| API Operation | HTTP Method & Action | Support Status | Notes |
|---|---|---|---|
| `AssumeRoleWithWebIdentity` | `POST /` (`Action=AssumeRoleWithWebIdentity`) | **Full** | Validates OIDC JWT token (`WebIdentityToken`) against configured JWKS, returning temporary S3 credentials (`ASIA...`, secret, session token, expiration). |

---

## Supported HTTP Headers

### Conditional Requests
- `If-Match: "<etag>"`
- `If-None-Match: "<etag>"`
- `If-Modified-Since: <HTTP-date>`
- `If-Unmodified-Since: <HTTP-date>`

### Range Requests
- `Range: bytes=0-1048575` (Standard byte range)
- `Range: bytes=1048576-` (Open-ended range to EOF)
- `Range: bytes=-524288` (Suffix range for tail of object)

### Checksum Verification
Validated on upload and returned in GET/HEAD responses:
- `x-amz-checksum-crc32`
- `x-amz-checksum-crc32c`
- `x-amz-checksum-sha1`
- `x-amz-checksum-sha256`

### Metadata & Object Lock
- `x-amz-meta-*`: User-defined object metadata (case-insensitive keys preserved).
- `x-amz-object-lock-mode`: `GOVERNANCE` or `COMPLIANCE`.
- `x-amz-object-lock-retain-until-date`: ISO-8601 UTC timestamp.
- `x-amz-object-lock-legal-hold`: `ON` or `OFF`.
- `x-amz-bypass-governance-retention`: `true` (allows users with `Admin` capability to mutate or delete protected objects).

---

## Authentication & Signature Support

| Scheme | Details |
|---|---|
| **AWS Signature Version 4 (SigV4)** | Full support for `Authorization: AWS4-HMAC-SHA256 Credential=...`. Verifies canonical request, scope, signed headers, and HMAC-SHA256 signature. |
| **Presigned URLs** | SigV4 query-string parameters (`X-Amz-Algorithm`, `X-Amz-Credential`, `X-Amz-Date`, `X-Amz-Expires`, `X-Amz-SignedHeaders`, `X-Amz-Signature`). |
| **Chunked Streaming** | `STREAMING-UNSIGNED-PAYLOAD-TRAILER` and chunked upload payloads used by `boto3` and AWS CLI v2. |
| **Anonymous Access** | Permitted for `GET` and `HEAD` operations on buckets configured with `PublicRead: true` (`x-amz-acl: public-read`). |

---

## Limitations & Unsupported Features

The following S3 features are intentionally out of scope or not supported:

- **Server-Side Encryption with KMS (SSE-KMS / SSE-C)**: Vessel3 stores content-addressed blobs unencrypted at rest; rely on host/volume-level encryption (LUKS, BitLocker) or TLS in flight.
- **Complex AWS IAM JSON Bucket Policies**: Bucket policies are capability-based (`Read`, `Write`, `Admin`) and managed via the Native API or canned ACLs (`public-read`, `private`).
- **Object-Level ACLs**: ACL endpoints evaluate permissions at the bucket level; per-object custom grantee ACLs return the bucket owner.
- **S3 Select**: SQL querying inside object bodies is not implemented.
- **S3 Cross-Region Replication**: Replication across independent clusters is not supported natively.
- **S3 Event Notifications**: Asynchronous webhook/SNS/SQS push notifications are not supported.
- **S3 Analytics, Inventory, & Metrics Configurations**: AWS cloud-specific analytics subresources are not implemented.
