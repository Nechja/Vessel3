# IAM & Access Control

Vessel3 includes a built-in multi-user Identity & Access Management (IAM) engine. It provides user management, scoped access keys, bucket ownership, and capability-based authorization across both the S3 wire protocol and the Native REST API.

---

## Identity Architecture

All identity state is persisted in an isolated SQLite database located at:
```
VESSEL3_DATA/iam/iam.db
```

The database tracks two primary entities:
1. **Users**: System identities characterized by a unique ID (`usr_<ulid>`), username, role, and status.
2. **Access Keys**: Credentials (`V3AK<16-char-id>`) with an associated secret key, optional expiration timestamp, and revocation flag.

---

## User Roles & Status

### User Roles

| Role | Permissions |
|---|---|
| **`Admin`** | Unrestricted access across the cluster. Can view, manage, and delete all buckets regardless of owner. Can manage IAM users and access keys, trigger storage GC, and run lifecycle sweeps. |
| **`Member`** | Standard tenant. Can create buckets, manage objects, configure bucket versioning and access on owned buckets. Cannot see or access buckets owned by other members unless granted by policy. |
| **`ReadOnly`** | Read-only consumer. Can read objects and list contents of buckets they own or public-read buckets. Cannot create buckets, write objects, or delete data. |

### User Status

| Status | Effect |
|---|---|
| **`Active`** | Normal operation. |
| **`Suspended`** | Immediate access lockout. All subsequent API calls and SigV4 requests using any of the user's access keys are rejected with `403 Forbidden` / `InvalidAccessKeyId`. |

---

## Access Keys

- **Key ID format**: Prefix `V3AK` followed by 16 alphanumeric characters (Base32 alphabet `A-Z2-7`), e.g., `V3AK9Q8R7S6T5U4V3W`.
- **Secret Key**: High-entropy cryptographically secure random token (40+ characters).
- **Time-to-Live (TTL)**: Optional expiration duration specified at key creation. Expired keys are rejected automatically.
- **Revocation**: Access keys can be revoked instantly without deleting the user. Revoked keys cannot be unrevoked.

---

## Bucket Ownership & Isolation

Every bucket in Vessel3 has an assigned `OwnerId`:
- **When created by a `Member`**: The bucket's owner is automatically set to the caller's `UserId`.
- **When created by an `Admin`**: The owner is set to an explicitly specified user ID (via API parameter), or defaults to the admin's own `UserId`.

### Scoped Listing Rules
- When a `Member` or `ReadOnly` user lists buckets (`GET /` in S3 or `GET /v1/buckets`), Vessel3 filters results: only buckets owned by that user are returned.
- When an `Admin` lists buckets, all buckets across all tenants are returned.

### Capability Evaluation Matrix

Vessel3 enforces three capabilities on bucket operations:

| Capability | Operations | Required Authorization |
|---|---|---|
| **`Read`** | `GetObject`, `HeadObject`, `ListObjects`, `GetBucketVersioning`, `GetBucketLocation` | Bucket is `PublicRead: true` **OR** caller is bucket owner **OR** caller has `Admin` role. |
| **`Write`** | `PutObject`, `DeleteObject`, `DeleteObjects`, `UploadPart`, `CopyObject` | Bucket is NOT `ReadOnly: true` **AND** caller role is NOT `ReadOnly` **AND** (caller is bucket owner **OR** caller has `Admin` role). |
| **`Admin`** | `DeleteBucket`, `SetAccess`, `PutBucketAcl`, `SetVersioning`, `SetLifecycle`, `SetCors`, `SetWebsite`, `SetObjectLock` | Caller is bucket owner **OR** caller has `Admin` role. |

---

## Bucket Access Configuration

Buckets maintain two policy flags configured via `PUT /v1/buckets/{bucket}/access` or S3 ACLs:

```json
{
  "publicRead": false,
  "readOnly": false
}
```

- **`publicRead: true`**: Permits unauthenticated (anonymous) callers and any authenticated tenant to perform `GET` and `HEAD` requests. Writing or modifying the bucket still requires ownership or admin credentials.
- **`readOnly: true`**: Blocks all mutations (`PUT`, `DELETE`) on the bucket for all users, including the owner. Useful for archiving or freezing datasets.

### S3 Canned ACL Mapping

When an S3 client sets a canned ACL via `x-amz-acl`, Vessel3 translates it directly:
- `x-amz-acl: public-read` sets `publicRead = true`.
- `x-amz-acl: private` sets `publicRead = false`.

---

## Bootstrapping & Promoting Administrators

Vessel3 offers both declarative configuration and runtime management to establish and promote administrators:

### 1. Declarative Admin Users (`VESSEL3_ADMIN_USERS`)
Specify a comma-separated list of usernames or substrings in your environment:
```env
VESSEL3_ADMIN_USERS=admin
```
- **"Contains-Name" Matching**: Matches both exact usernames and case-insensitive substrings. For example, `person` will match OIDC/SSO subjects like `acct_person.dIftEd_eU48bcFmhcaiAJA` or `person@example.com`.
- **Startup Reconciliation**: On server boot, Vessel3 scans `iam.db` and immediately promotes any matching users to the `Admin` role.
- **Login Capture**: When users authenticate via OIDC, matching subjects are automatically provisioned as or promoted to `Admin`.

### 2. Root Access Key Bootstrap
When Vessel3 starts with `VESSEL3_ACCESS_KEY` and `VESSEL3_SECRET_KEY` configured:
1. It inspects `iam/iam.db` for a user with `username = 'admin'`.
2. If missing, it creates the `admin` user with role `Admin` and status `Active`.
3. If an access key with the specified ID does not exist, it inserts the bootstrap key paired with the provided secret.

### 3. Web UI IAM Directory (`/_ui/iam`)
Administrators can manage users and credentials visually:
- **Role & Status Editing**: Click **edit** on any user row to switch their role (`Admin`, `Member`, `ReadOnly`) or status (`Active`, `Suspended`).
- **User Creation & Deletion**: Create new local users with specific initial roles, or remove accounts.
- **Access Key Management**: Generate new access keys with optional descriptions and TTL expirations, or revoke active keys instantly.

### 4. Native REST API Role Management
Administrators can update user roles and statuses programmatically:
```http
PUT /v1/iam/users/{userId}/role
Content-Type: application/json
Authorization: Vessel <admin-key>:<secret>

{"role": "Admin"}
```

```http
PUT /v1/iam/users/{userId}/status
Content-Type: application/json
Authorization: Vessel <admin-key>:<secret>

{"status": "Suspended"}
```

