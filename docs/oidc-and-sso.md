# OIDC & Single Sign-On (SSO)

Vessel3 integrates with OpenID Connect (OIDC) identity providers (Keycloak, Authentik, Dex, Okta, GitLab, GitHub Actions) to enable federated authentication across three interfaces:
1. **AWS SDKs & CLI**: Via STS `AssumeRoleWithWebIdentity`.
2. **Native REST API**: Via `Authorization: Bearer <jwt>` with automatic Just-In-Time (JIT) user account provisioning.
3. **Embedded Web UI (`/_ui`)**: Via browser-based Authorization Code Flow with PKCE.

---

## Configuration Variables

| Variable | Description |
|---|---|
| `VESSEL3_OIDC_ISSUER` | Base URL of the OIDC provider (e.g. `https://auth.example.com/realms/master`). Must serve `/.well-known/openid-configuration`. |
| `VESSEL3_OIDC_CLIENT_ID` | Client ID configured in the provider. |
| `VESSEL3_OIDC_AUDIENCE` | *(Optional)* Additional audience string accepted in JWTs. |
| `VESSEL3_OIDC_REQUIRE_CLAIM` | *(Optional)* Claim assertion in `name=value` format required to authenticate. |
| `VESSEL3_OIDC_ADMIN_CLAIM` | *(Optional)* Claim assertion in `name=value` format (e.g. `roles=admin` or `groups=storage-admins`) that automatically grants the `Admin` role. |
| `VESSEL3_ADMIN_USERS` | *(Optional)* Comma-separated list of usernames or substring patterns (e.g. `admin,kayla`) automatically granted `Admin` role on boot and OIDC login. |

---

## 1. AWS SDK & CLI Integration (STS)

Any AWS SDK or tool that implements web identity federation (e.g., in Kubernetes with IRSA or GitHub Actions) works directly against Vessel3's STS endpoint.

### CLI Example

```sh
export ENDPOINT=http://127.0.0.1:9000
export TOKEN="eyJhbGciOi..."

aws --endpoint-url $ENDPOINT sts assume-role-with-web-identity \
  --role-arn arn:aws:iam::0:role/vessel3 \
  --role-session-name federated-user \
  --web-identity-token "$TOKEN" \
  --duration-seconds 3600
```

**Response**:
```json
{
  "Credentials": {
    "AccessKeyId": "ASIAEXAMPLE123456",
    "SecretAccessKey": "...",
    "SessionToken": "...",
    "Expiration": "2026-09-27T13:00:00Z"
  }
}
```

Sessions default to 1 hour (range 15 minutes to 12 hours) and are tracked in memory.

---

## 2. Native API Bearer Token Authentication & JIT Provisioning

When a request arrives at `/v1/...` with `Authorization: Bearer <jwt>`:
1. Vessel3 validates the token signature using the provider's JWKS (RS256 and ES256 supported).
2. It verifies `issuer`, `expiration`, `audience`, and optional claim constraints (`VESSEL3_OIDC_REQUIRE_CLAIM`).
3. **Admin Role Evaluation**:
   - Vessel3 checks if the token subject matches any substring pattern in `VESSEL3_ADMIN_USERS` (e.g., `kayla` matching `acct_kayla.dIftEd_eU48bcFmhcaiAJA`).
   - It also checks if the token contains the claim configured in `VESSEL3_OIDC_ADMIN_CLAIM` (e.g., `groups=vessel3-admins`).
   - If either matches, the user is targeted for the **`Admin`** role. Otherwise, they receive the **`Member`** role.
4. **Just-In-Time (JIT) Provisioning & Promotion**:
   - Vessel3 checks `iam/iam.db` for an existing user matching the token's `subject` (`sub`).
   - If missing, a new user is created automatically with their evaluated role (`Admin` or `Member`) and status `Active`.
   - If the user already exists as a `Member` and qualifies for `Admin`, Vessel3 immediately promotes them in the database to `Admin`.
5. Subsequent requests by the federated user share their assigned user ID and ownership scope.

---

## 3. Web UI Single Sign-On (PKCE)

When `VESSEL3_OIDC_ISSUER` is configured, the embedded Web UI automatically activates SSO:
- The UI initiates an Authorization Code grant with PKCE (`code_challenge_method=S256`).
- Upon callback, it exchanges the authorization code for an ID token.
- It authenticates into the S3 and Native APIs using the federated session.

### Provider Client Registration

Register a public client in your identity provider with:
- **Client Type**: Public (no client secret)
- **Redirect URI**: `<vessel3-origin>/_ui/` (e.g., `https://s3.example.com/_ui/`)
- **Post-Logout Redirect URI**: `<vessel3-origin>/_ui/`
- **Allowed Web Origins (CORS)**: `<vessel3-origin>`

---

## Provider Configuration Recipes

### Keycloak

```env
VESSEL3_OIDC_ISSUER=https://keycloak.example.com/realms/production
VESSEL3_OIDC_CLIENT_ID=vessel3
VESSEL3_OIDC_REQUIRE_CLAIM=groups=storage-users
```
In Keycloak:
- Create client `vessel3`.
- Set Client authentication to `Off` (Public Client).
- Valid redirect URIs: `https://s3.example.com/_ui/*`.
- Web origins: `https://s3.example.com`.

### Authentik

```env
VESSEL3_OIDC_ISSUER=https://authentik.example.com/application/o/vessel3/
VESSEL3_OIDC_CLIENT_ID=vessel3-client-id
```
In Authentik:
- Create an OAuth2/OpenID Provider.
- Client type: `Public`.
- Redirect URI: `https://s3.example.com/_ui/`.
- Create Application binding to the provider.
