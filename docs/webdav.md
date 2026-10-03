# WebDAV Protocol & Network Drive Mounting

Vessel3 includes native support for WebDAV (RFC 4918, Class 1 and Class 2 compliance) on the standard server port at `/dav/` and `/webdav/`. This enables mounting Vessel3 buckets directly as local network drives in Windows Explorer, macOS Finder, Linux desktop file managers, command-line utilities, and mobile sync applications without requiring third-party gateway software.

WebDAV operations share the same content-addressed immutable blob pool, SQLite metadata indices, and multi-user IAM authorization model as S3, Azure Blob Storage, and OCI Container Repos.

---

## Key Capabilities

- **Native OS Network Drive Mounting**: Mount Vessel3 directly on Windows (Map Network Drive), macOS (Finder "Connect to Server"), and Linux (`davfs2`, GNOME Files, KDE Dolphin).
- **RFC 4918 Compliance**:
  - `OPTIONS`: Class 1 and Class 2 discovery with `DAV: 1, 2` and `MS-Author-Via: DAV` headers.
  - `PROPFIND`: Multi-Status (207) XML property discovery for service root, bucket root, and directory hierarchies with `Depth: 0` and `Depth: 1`.
  - `GET` & `HEAD`: Full content retrieval and Range requests for streaming media.
  - `PUT`: Direct file upload and overwrite.
  - `MKCOL`: Bucket creation and directory collection markers.
  - `DELETE`: Atomic deletion of files, directories, and buckets.
  - `COPY` & `MOVE`: File and collection copying and moving with `Overwrite` header support (`T`/`F`).
  - `LOCK` & `UNLOCK`: Class 2 lock responder with opaque UUID tokens ensuring compatibility with Microsoft Office and Windows Explorer webclient redirectors.
  - `PROPPATCH`: Property update responders for desktop shell metadata caching.
- **IAM Integration & Multi-User Isolation**:
  - Authenticates via standard HTTP Basic Authentication.
  - Supports both bootstrap admin keys (`VESSEL3_ACCESS_KEY` / `VESSEL3_SECRET_KEY`) and tenant user access keys generated via IAM.
  - Tenant isolation: users can only view, read, and write to buckets they own or have explicit capability for.
  - `ReadOnly` user role enforced across all WebDAV verbs: read and browse allowed, write/delete/create blocked with 403 Forbidden.

---

## Configuration

WebDAV is enabled by default.

| Variable | Type | Default | Description |
|---|---|---|---|
| `VESSEL3_WEBDAV_ENABLED` | Boolean | `true` | Enables or disables WebDAV endpoints on the server. |

---

## Mounting & Client Setup Recipes

### 1. Windows File Explorer

Windows WebClient service natively mounts WebDAV shares.

#### Via File Explorer GUI:
1. Open **File Explorer** and click **This PC**.
2. Click **Map network drive** in the top ribbon (or three dots menu on Windows 11).
3. Select a drive letter (e.g. `Z:`).
4. In **Folder**, enter:
   ```text
   http://localhost:9000/dav/
   ```
   Or mount a specific bucket directly:
   ```text
   http://localhost:9000/dav/my-bucket/
   ```
5. Check **Connect using different credentials**.
6. When prompted, enter your Vessel3 Access Key as **Username** and Secret Key as **Password**.

#### Via Command Prompt (`net use`):
```cmd
net use Z: http://localhost:9000/dav/ /user:<ACCESS_KEY> <SECRET_KEY> /persistent:yes
```

> [!NOTE]
> By default, Windows disables Basic authentication over unencrypted HTTP for WebDAV. When testing on localhost or over plain HTTP without HTTPS/TLS, set the Windows registry key `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WebClient\Parameters\BasicAuthLevel` to `2` (allows Basic Auth over HTTP and HTTPS) and restart the `WebClient` service: `net stop webclient && net start webclient`. In production, place Caddy or an SSL reverse proxy in front of Vessel3.

---

### 2. macOS Finder

1. In macOS Finder, press **Cmd + K** (or click **Go** -> **Connect to Server...**).
2. Enter the server URL:
   ```text
   http://localhost:9000/dav/
   ```
3. Click **Connect**.
4. Select **Registered User**.
5. Enter your Vessel3 Access Key in **Name** and Secret Key in **Password**.
6. Your Vessel3 buckets will appear as mounted network folders on your Desktop and Finder sidebar.

---

### 3. Linux `davfs2` (FSTAB & Command Line)

Install `davfs2`:
```bash
sudo apt install davfs2    # Debian/Ubuntu
sudo dnf install davfs2    # Fedora/RHEL
sudo pacman -S davfs2      # Arch
```

Mount to a local directory:
```bash
sudo mkdir -p /mnt/vessel
sudo mount -t davfs http://localhost:9000/dav/ /mnt/vessel -o username=<ACCESS_KEY>,password=<SECRET_KEY>
```

Add to `/etc/fstab` for persistent boot mounting:
```text
http://localhost:9000/dav/ /mnt/vessel davfs rw,user,noauto 0 0
```
Store credentials in `~/.davfs2/secrets`:
```text
http://localhost:9000/dav/ <ACCESS_KEY> <SECRET_KEY>
```

---

### 4. Linux Desktop File Managers (GNOME / KDE)

#### GNOME Files (Nautilus):
1. Open Files and select **+ Other Locations**.
2. In **Connect to Server**, enter:
   ```text
   dav://localhost:9000/dav/
   ```
3. Enter your Access Key and Secret Key when prompted.

#### KDE Dolphin:
In the path bar or network places, enter:
```text
webdav://localhost:9000/dav/
```

---

### 5. Rclone

Add a WebDAV remote to `rclone.conf`:
```ini
[vessel-webdav]
type = webdav
url = http://localhost:9000/dav
vendor = other
user = <VESSEL3_ACCESS_KEY>
pass = <RCLONE_OBFUSCATED_SECRET>
```

Test listing buckets:
```bash
rclone lsd vessel-webdav:
```

Sync files:
```bash
rclone copy ./documents vessel-webdav:backup/documents
```

---

### 6. Mobile & Desktop Sync Apps

- **Nextcloud Desktop / Mobile Client**: Configure external storage as WebDAV pointing to `http://<server>:9000/dav/<bucket>/`.
- **PhotoSync (iOS / Android)**: Choose WebDAV target, URL `http://<server>:9000/dav/photos/`, enter credentials to auto-backup photos.
- **Cryptomator**: Create client-side encrypted vaults stored directly on Vessel3 via WebDAV.
- **Cyberduck / Mountain Duck**: Select WebDAV (HTTP/HTTPS) and connect with access key credentials.

---

## URL Structure

| URL | Description | Behavior |
|---|---|---|
| `/dav/` | Service Root | Lists all accessible buckets as top-level collections in Multi-Status (207) response. |
| `/dav/{bucket}/` | Bucket Root | Lists contents and subdirectories of the specified bucket. |
| `/dav/{bucket}/{path}` | Object Resource | Performs GET, PUT, HEAD, DELETE, COPY, MOVE, LOCK, UNLOCK on a specific object. |
| `/dav/{bucket}/{folder}/` | Directory Collection | Targets a directory marker or prefix within the specified bucket. |
| `/webdav/...` | Alias Endpoint | Exact alias of `/dav/...`. |
