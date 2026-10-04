# Native REST API

In addition to the Amazon S3 wire protocol, Vessel3 exposes a lightweight, native JSON REST API under `/v1`. It enables programmatic management of IAM identities, access keys, buckets, objects, and storage maintenance sweeps.

---

## Authentication

Every request to `/v1/...` must provide authentication, with the exception of `GET` and `HEAD` on objects residing in buckets configured with `publicRead: true`.

Vessel3 accepts credentials through three header schemes:

### 1. HTTP Bearer Token (OIDC JWT)
```http
Authorization: Bearer <jwt-token>
```

### 2. Vessel Auth Header
```http
Authorization: Vessel <access-key-id>:<secret-key>
```

### 3. Custom Header Pair
```http
X-Vessel-Key: <access-key-id>
X-Vessel-Secret: <secret-key>
```

---

## Error Handling

All non-2xx responses return a standardized JSON error body:

```json
{
  "error": "AccessDenied",
  "message": "Admin role required"
}
```

Common error codes and status mappings:
- `400 Bad Request`: `InvalidArgument`, `InvalidBucketName`
- `401 Unauthorized`: `Unauthorized`, `InvalidCredentials`, `ExpiredToken`
- `403 Forbidden`: `AccessDenied`
- `404 Not Found`: `NoSuchBucket`, `NoSuchKey`, `UserNotFound`, `KeyNotFound`
- `409 Conflict`: `BucketNotEmpty`, `BucketAlreadyExists`

---

## Identity & IAM Endpoints

### Get Current Caller (`WhoAmI`)
```http
GET /v1/iam/whoami
```
**Response: `200 OK`**
```json
{
  "userId": "usr_01J8R6T3Y",
  "username": "alice",
  "role": "Member",
  "accessKeyId": "V3AK9Q8R7S6T5U4V3W"
}
```

### List Users (Admin only)
```http
GET /v1/iam/users
```
**Response: `200 OK`**
```json
[
  {
    "id": "usr_01J8R6T3Y",
    "username": "alice",
    "role": "Member",
    "status": "Active",
    "createdAt": "2026-09-26T12:00:00Z"
  }
]
```

### Create User (Admin only)
```http
POST /v1/iam/users
Content-Type: application/json

{
  "username": "charlie",
  "role": "Member"
}
```
*`role` can be `"Admin"`, `"Member"`, or `"ReadOnly"` (defaults to `"Member"`).*

**Response: `201 Created`**
```json
{
  "id": "usr_01J8R8Z4X",
  "username": "charlie",
  "role": "Member",
  "status": "Active",
  "createdAt": "2026-09-27T10:00:00Z"
}
```

### Get User
```http
GET /v1/iam/users/{userId}
```
*Permitted for `Admin` callers or the user matching `{userId}`.*

**Response: `200 OK`** with User JSON object.

### Delete User (Admin only)
```http
DELETE /v1/iam/users/{userId}
```
**Response: `204 No Content`**

### Update User Role (Admin only)
```http
PUT /v1/iam/users/{userId}/role
Content-Type: application/json

{
  "role": "Admin"
}
```
*`role` can be `"Admin"`, `"Member"`, or `"ReadOnly"`.*

**Response: `204 No Content`**

### Update User Status (Admin only)
```http
PUT /v1/iam/users/{userId}/status
Content-Type: application/json

{
  "status": "Suspended"
}
```
*`status` can be `"Active"` or `"Suspended"`.*

**Response: `204 No Content`**


### List Access Keys for User
```http
GET /v1/iam/users/{userId}/keys
```
*Permitted for `Admin` callers or the user matching `{userId}`.*

**Response: `200 OK`**
```json
[
  {
    "id": "V3AK9Q8R7S6T5U4V3W",
    "secretKey": "...",
    "userId": "usr_01J8R6T3Y",
    "description": "CI Pipeline Key",
    "createdAt": "2026-09-26T12:05:00Z",
    "expiresAt": null,
    "isRevoked": false
  }
]
```

### Create Access Key
```http
POST /v1/iam/users/{userId}/keys
Content-Type: application/json

{
  "description": "Backup Worker",
  "ttlSeconds": 2592000
}
```
*`ttlSeconds` is optional. If omitted, the key does not expire.*

**Response: `201 Created`** with AccessKey JSON object.

### Revoke Access Key
```http
DELETE /v1/iam/keys/{accessKeyId}
```
*Permitted for `Admin` callers or the key's owner.*

**Response: `204 No Content`**

---

## Bucket Management Endpoints

### List Buckets
```http
GET /v1/buckets
```
*Returns all buckets for `Admin` callers; returns only owned buckets for other callers.*

**Response: `200 OK`**
```json
[
  {
    "name": "media",
    "createdAt": "2026-09-26T14:00:00Z",
    "ownerId": "usr_01J8R6T3Y"
  }
]
```

### Create Bucket
```http
PUT /v1/buckets/{bucket}
```
**Response: `200 OK`**

### Delete Bucket
```http
DELETE /v1/buckets/{bucket}
```
*Requires `Admin` capability (owner or admin role). Bucket must be empty.*

**Response: `204 No Content`**

### Get Bucket Access Policy
```http
GET /v1/buckets/{bucket}/access
```
**Response: `200 OK`**
```json
{
  "publicRead": false,
  "readOnly": false
}
```

### Update Bucket Access Policy
```http
PUT /v1/buckets/{bucket}/access
Content-Type: application/json

{
  "publicRead": true,
  "readOnly": false
}
```
**Response: `200 OK`**

### Get Bucket Versioning Status
```http
GET /v1/buckets/{bucket}/versioning
```
**Response: `200 OK`**
```json
{
  "status": "Enabled"
}
```
*Status values: `"Off"`, `"Enabled"`, `"Suspended"`.*

### Update Bucket Versioning
```http
PUT /v1/buckets/{bucket}/versioning
Content-Type: application/json

{
  "status": "Enabled"
}
```
**Response: `200 OK`**

---

## Object Endpoints

### List Objects
```http
GET /v1/buckets/{bucket}/objects?prefix=logs/&delimiter=/&limit=100
```
Query Parameters:
- `prefix` (optional): Filter keys by prefix.
- `delimiter` (optional): Group common folder prefixes.
- `marker` (optional): Pagination marker.
- `limit` (optional): Max entries to return (default: `1000`).

**Response: `200 OK`**
```json
{
  "objects": [
    {
      "key": "logs/app.log",
      "size": 4096,
      "eTag": "b10a8db164e0754105b7a99be72e3fe5",
      "lastModified": "2026-09-27T08:30:00Z",
      "versionId": null
    }
  ],
  "prefixes": ["logs/archived/"],
  "isTruncated": false,
  "nextMarker": null
}
```

### Put Object
```http
PUT /v1/buckets/{bucket}/objects/{**key}
Content-Type: image/jpeg
X-Vessel-Meta-Photographer: Ansel

<binary stream>
```
*Custom metadata must be prefixed with `X-Vessel-Meta-`.*

**Response: `200 OK`**
```json
{
  "eTag": "b10a8db164e0754105b7a99be72e3fe5",
  "versionId": "01J8RAB654...",
  "size": 1048576,
  "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}
```

### Get Object
```http
GET /v1/buckets/{bucket}/objects/{**key}?versionId={versionId}
```
**Response: `200 OK`** with streamed object payload and response headers:
- `ETag: "<etag>"`
- `Content-Type: <type>`
- `Content-Length: <size>`
- `Last-Modified: <http-date>`
- `X-Vessel-Version-Id: <version-id>`
- `X-Vessel-Meta-<name>: <value>`

### Stat Object (Metadata only)
```http
HEAD /v1/buckets/{bucket}/objects/{**key}?versionId={versionId}
```
**Response: `200 OK`** with headers identical to `GET`, but without response body.

### Delete Object
```http
DELETE /v1/buckets/{bucket}/objects/{**key}?versionId={versionId}
```
*If `versionId` is omitted on versioned buckets, a delete marker is placed. If specified, the specific version is permanently deleted.*

**Response: `204 No Content`**

---

## Storage Maintenance Endpoints (Admin only)

### Trigger Garbage Collection
```http
POST /v1/admin/gc?minBlobAgeSec=3600&minUploadAgeSec=604800
```
Query parameters:
- `minBlobAgeSec`: Grace period in seconds for unreferenced blobs (default: `3600`).
- `minUploadAgeSec`: Grace period in seconds for abandoned multipart uploads (default: `604800` = 7 days).

**Response: `200 OK`**
```json
{
  "blobsDeleted": 142,
  "uploadsReaped": 3
}
```

### Trigger Lifecycle Sweep
```http
POST /v1/admin/sweep
```
Query parameters:
- `now` (optional): Simulated UTC ISO-8601 timestamp for testing lifecycle expiration.

**Response: `200 OK`**
```json
{
  "expired": 85,
  "markersReaped": 12
}
```

### Query Server & Access Logs
```http
GET /v1/admin/logs?limit=100&level=Error&protocol=s3
```
Query parameters:
- `limit` (optional): Maximum log entries to return (default: `100`, max: `1000`).
- `level` (optional): Filter by log level (`Information`, `Warning`, `Error`).
- `protocol` (optional): Filter by protocol engine (`s3`, `azure`, `oci`, `native`, `webdav`).

**Response: `200 OK`**
```json
[
  {
    "id": "c1f7b03b708d4b319aa53e9a78123456",
    "timestamp": "2026-10-04T19:00:00.000Z",
    "level": "Information",
    "source": "Access",
    "message": "PUT /v1/buckets/my-bucket -> 200",
    "protocol": "native",
    "action": "CreateBucket",
    "subject": "my-bucket",
    "actor": "admin",
    "statusCode": 200,
    "durationMs": 14.2,
    "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    "errorDetails": null
  }
]
```

### Clear Server Logs
```http
DELETE /v1/admin/logs
```
Flushes all entries from the in-memory server log buffer.

**Response: `204 No Content`**


---

## Webhook Endpoints

Vessel3 provides native REST endpoints for managing webhooks, testing deliveries, and exporting configurations. Requires `Admin` or write-capable IAM credentials.

### List Webhooks
```http
GET /v1/webhooks
```
**Response: `200 OK`**
```json
[
  {
    "id": "whk_01J8R6T3Y9",
    "name": "CI Deploy",
    "url": "https://ci.example.com/hooks",
    "secret": "rainier-secret",
    "eventFilters": ["container.image.pushed"],
    "resourceFilters": ["drummer:*"],
    "active": true,
    "createdAt": "2026-10-03T12:00:00Z",
    "lastTriggeredAt": "2026-10-03T12:05:00Z",
    "lastStatusCode": 200,
    "lastError": null,
    "isStatic": false
  }
]
```

### Get Webhook by ID
```http
GET /v1/webhooks/{id}
```
**Response: `200 OK`** or `404 Not Found`

### Create Webhook
```http
POST /v1/webhooks
Content-Type: application/json

{
  "name": "ArgoCD Auto Sync",
  "url": "https://argo.example.com/events",
  "secret": "hood-secret",
  "eventFilters": ["container.image.pushed"],
  "resourceFilters": ["drummer:*"],
  "active": true
}
```
**Response: `201 Created`**

### Update Webhook
```http
PUT /v1/webhooks/{id}
Content-Type: application/json

{
  "name": "ArgoCD Auto Sync",
  "url": "https://argo.example.com/events-v2",
  "secret": "hood-secret",
  "eventFilters": ["container.*"],
  "resourceFilters": ["*"],
  "active": true
}
```
**Response: `200 OK`**

### Delete Webhook
```http
DELETE /v1/webhooks/{id}
```
*Returns `400 Bad Request` if the webhook was loaded statically from `webhooks.yaml`.*

**Response: `204 No Content`**

### Test Webhook (Ping)
Dispatches a mock `vessel.ping` event to the target URL and measures round-trip latency and response code.
```http
POST /v1/webhooks/{id}/test
```
**Response: `200 OK`**
```json
{
  "webhookId": "whk_01J8R6T3Y9",
  "success": true,
  "statusCode": 200,
  "latencyMs": 35.8,
  "errorMessage": null,
  "responseBody": "OK"
}
```

### Export Webhooks as YAML
Returns all active webhooks formatted as declarative YAML suitable for `webhooks.yaml` or GitOps repositories.
```http
GET /v1/webhooks/export.yaml
```
**Response: `200 OK` (`text/yaml; charset=utf-8`)**

---

## Event Streaming Endpoints

Vessel3 supports real-time event streaming via Server-Sent Events (SSE) adhering to the CloudEvents v1.0 standard. Clients can subscribe to domain events across object mutations, bucket lifecycle, and container image pushes.

### Stream Domain Events (SSE)
```http
GET /v1/events/stream?topics=object.created,container.image.pushed&resource=photos/*
```
Query parameters:
- `topics` (optional): Comma-separated list or wildcard pattern of event types to subscribe to (e.g. `object.created`, `container.*`, or `*`). Default: `*`.
- `resource` (optional): Resource target filter or wildcard prefix (e.g. `bucket-name/*`, `container-repo:*`, or `*`). Default: `*`.

**Headers:**
- `Accept: text/event-stream`
- Authentication header required (`Authorization: Bearer ...` or S3/Native access key).

**Response: `200 OK` (`text/event-stream`)**
```text
: connected

event: object.created
id: evt_01J8R8W1XYZ
data: {"id":"evt_01J8R8W1XYZ","source":"/vessel3/us-east-1","type":"object.created","resource":"photos/vacation.jpg","time":"2026-10-04T19:15:30.123Z","data":{"size":2048576,"etag":"a5c8...","versionId":null}}

: ping
```
*Note: Vessel3 sends a periodic `: ping` comment every 15 seconds to prevent intermediate proxy timeouts.*


