# Azure Blob Storage Protocol Support & Client Guide

Vessel3 implements native Azure Blob Storage wire compatibility over HTTP/1.1 and HTTP/2. Existing Azure SDKs (.NET, Python, Go, JavaScript/TypeScript, Java), Azure CLI, AzCopy, and Azure Storage Explorer can communicate directly with Vessel3 without modifying client code.

---

## Overview & Architecture

Vessel3 implements Azure Blob Storage compatibility via an inbound driving adapter (`Vessel3.Protocols.Azure`) adhering strictly to hexagonal architecture:

- **Single-Port Multiplexing**: Vessel3 serves Azure requests concurrently with Amazon S3 (`SigV4`), OCI Container Registry (`/v2/...`), and Native REST (`/v1/...`) on the same listening port (`9000`).
- **Drop-in Azurite Emulation**: Preconfigured support for the standard Azure Storage emulator account `devstoreaccount1`, allowing tests and local development setups targeting Azurite to work out-of-the-box.
- **Vessel3 IAM Integration**: Native support for Vessel3-managed access keys and secret keys. Requests are authorized against Vessel3's capability model (`Read`, `Write`, `Admin`) and scoped to bucket/container ownership.
- **Content-Addressed Storage**: All blobs—whether uploaded as single-shot Block Blobs or staged chunks via `PutBlock`/`PutBlockList`—are stored in Vessel3's immutable, content-addressed blob pool with atomic fsync durability.

---

## Verified Client SDKs & Tools

| Client / Tool | Package / Distribution | Tested / Target Version | Target Wire Version (`x-ms-version`) |
|---|---|---|---|
| **.NET SDK** | `Azure.Storage.Blobs` | **v12.30.0** (tested in CI probe) / `12.x` | `2024-11-04`, `2026-10-06` |
| **Python SDK** | `azure-storage-blob` | `v12.19+` | `2023-11-03`, `2024-11-04` |
| **Go SDK** | `azblob` (`azure-sdk-for-go`) | `v1.3+` | `2023-11-03+` |
| **JavaScript / TypeScript** | `@azure/storage-blob` | `v12.17+` | `2023-11-03+` |
| **AzCopy** | `azcopy` CLI | `v10.20+` | `2020-10-02+` |
| **Azure CLI** | `az storage blob` | `v2.50+` | `2020-10-02+` |
| **Azure Storage Explorer** | Microsoft Azure Storage Explorer | `v1.30+` | `2020-10-02+` |

### Supported REST API Versions
Vessel3 accepts and handles requests specifying any `x-ms-version` from **`2020-10-02`** through **`2026-10-06`**, defaulting to `2026-10-06` if omitted.

---

## Protocol Support Matrix

### Service Operations

| API Operation | HTTP Method & URI | Support Status | Notes |
|---|---|---|---|
| `ListContainers` | `GET /?comp=list` | **Full** | Enumerates containers. Returns `EnumerationResults` XML. Scoped by caller identity: non-admins only see containers they own; admins see all containers. Supports `prefix`, `marker`, `maxresults`. |
| `GetServiceProperties` | `GET /?restype=service&comp=properties` | **Full** | Returns XML `StorageServiceProperties` with default logging, hour metrics, minute metrics, and CORS settings. |
| `GetAccountInfo` | `GET /?restype=account&comp=properties` | **Full** | Returns account headers (`x-ms-account-kind: StorageV2`, `x-ms-sku-name: Standard_LRS`). |

---

### Container Operations

| API Operation | HTTP Method & Subresource | Support Status | Notes |
|---|---|---|---|
| `CreateContainer` | `PUT /{container}?restype=container` | **Full** | Creates a container/bucket. Caller becomes the container owner. Sets public access via `x-ms-blob-public-access` (`container`, `blob`, or `private`). |
| `GetContainerProperties` | `GET /{container}?restype=container`<br>`HEAD /{container}?restype=container` | **Full** | Returns container existence and lease status (`unlocked`, `available`), ETag, and timestamps. |
| `DeleteContainer` | `DELETE /{container}?restype=container` | **Full** | Deletes an empty container. Requires `Admin` capability (owner or admin). |
| `ListBlobs` | `GET /{container}?restype=container&comp=list` | **Full** | Enumerates blobs and virtual directories. Returns `EnumerationResults` XML. Supports `prefix`, `delimiter`, `marker`, `maxresults`, and pagination. |

---

### Blob Operations

| API Operation | HTTP Method & Subresource | Support Status | Notes |
|---|---|---|---|
| `PutBlob` | `PUT /{container}/{blob}` | **Full** | Creates single-shot `BlockBlob`. Streams payload to disk, calculates digests, fsyncs, and commits atomically. Supports `Content-Type`, `Content-MD5`, and `x-ms-meta-*`. |
| `GetBlob` | `GET /{container}/{blob}` | **Full** | Streams blob from content-addressed pool. Supports standard HTTP `Range` and `x-ms-range` headers for byte-range reads. Sets `ETag`, `Last-Modified`, and user metadata headers. |
| `HeadBlob` (Get Properties) | `HEAD /{container}/{blob}` | **Full** | Returns blob metadata, `Content-Length`, `Content-Type`, `ETag`, `Last-Modified`, `x-ms-blob-type: BlockBlob`, and `x-ms-meta-*`. |
| `DeleteBlob` | `DELETE /{container}/{blob}` | **Full** | Removes the current version of the blob. In versioned buckets, records a tombstone. |

---

### Block Staging Operations (Chunked / Parallel Uploads)

| API Operation | HTTP Method & Subresource | Support Status | Notes |
|---|---|---|---|
| `PutBlock` | `PUT /{container}/{blob}?comp=block&blockid={base64Id}` | **Full** | Stages a block via `IChunkStager`. Stores block payload into blob pool and records staged chunk mapping. Returns `201 Created` with `Content-MD5`. |
| `PutBlockList` | `PUT /{container}/{blob}?comp=blocklist` | **Full** | Assembles staged blocks in the specified order into a final object. Accepts `<BlockList>` XML (`<Latest>`, `<Uncommitted>`, `<Committed>`). Updates content type and user metadata on commit. |

---

### Supported Headers & Features

- **Authentication**: `SharedKey` and `SharedKeyLite` HMAC-SHA256 signature verification.
- **Client Tracing**: `x-ms-client-request-id` is echoed back in responses, and unique `x-ms-request-id` is generated for every request.
- **Version Compatibility**: Supports `x-ms-version` headers from Azure SDKs (e.g., `2020-10-02`, `2023-11-03`, `2024-11-04`, `2026-10-06`).
- **Range Slicing**: Full HTTP byte range support via standard `Range: bytes=start-end` or Azure-specific `x-ms-range: bytes=start-end` (returns `206 Partial Content` with `Content-Range`).
- **User Metadata**: Custom metadata keys and values via `x-ms-meta-<name>` on PUT, GET, and HEAD.
- **Detailed Error Messages**: Returns Azure-compliant XML error envelopes including `<AuthenticationErrorDetail>` on signature mismatch for easy client debugging.

---

## Client Configuration & Connection Strings

### 1. Azurite-Compatible Local Emulator String
To use Vessel3 as a drop-in replacement for the Azurite storage emulator:

```ini
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:9000/devstoreaccount1;
```

### 2. Vessel3 IAM Access Key Connection String
To connect using credentials created in Vessel3's IAM:

```ini
DefaultEndpointsProtocol=http;AccountName=<Vessel3_Access_Key_Id>;AccountKey=<Vessel3_Secret_Key>;BlobEndpoint=http://127.0.0.1:9000/<Vessel3_Access_Key_Id>;
```

---

## Tooling & Code Recipes

### .NET (`Azure.Storage.Blobs`)

```csharp
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

var endpoint = "http://127.0.0.1:9000/devstoreaccount1";
var account = "devstoreaccount1";
var key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

var credential = new StorageSharedKeyCredential(account, key);
var serviceClient = new BlobServiceClient(new Uri(endpoint), credential);

// 1. Create a container
var containerClient = serviceClient.GetBlobContainerClient("data-archive");
await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

// 2. Upload a single block blob
var blobClient = containerClient.GetBlobClient("report.pdf");
await blobClient.UploadAsync(File.OpenRead("report.pdf"), new BlobUploadOptions
{
    HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
});

// 3. Chunked / staged block upload for large files
var blockBlobClient = containerClient.GetBlockBlobClient("dataset.tar.gz");
var blockId1 = Convert.ToBase64String(Encoding.UTF8.GetBytes("block-00001"));
var blockId2 = Convert.ToBase64String(Encoding.UTF8.GetBytes("block-00002"));

await blockBlobClient.StageBlockAsync(blockId1, chunk1Stream);
await blockBlobClient.StageBlockAsync(blockId2, chunk2Stream);
await blockBlobClient.CommitBlockListAsync(new[] { blockId1, blockId2 });

// 4. Download with byte-range read
var rangeResponse = await blobClient.DownloadStreamingAsync(new BlobDownloadOptions
{
    Range = new Azure.HttpRange(0, 1024)
});
```

---

### Python (`azure-storage-blob`)

```python
from azure.storage.blob import BlobServiceClient

connection_string = (
    "DefaultEndpointsProtocol=http;"
    "AccountName=devstoreaccount1;"
    "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
    "BlobEndpoint=http://127.0.0.1:9000/devstoreaccount1;"
)

client = BlobServiceClient.from_connection_string(connection_string)

# 1. Create container
container = client.create_container("telemetry")

# 2. Upload blob
blob = container.get_blob_client("metrics.json")
blob.upload_blob(b'{"cpu": 12.4, "ram": 48.1}', overwrite=True)

# 3. List blobs in container
for item in container.list_blobs():
    print(f"Found blob: {item.name}, size: {item.size} bytes")

# 4. Download blob
stream = blob.download_blob()
print(stream.readall().decode("utf-8"))
```

---

### AzCopy

Set your connection credentials in environment variables:

```sh
export AZCOPY_ACCOUNT_NAME="devstoreaccount1"
export AZCOPY_ACCOUNT_KEY="Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="

# 1. Copy a local file to Vessel3
azcopy copy "./backup.tar.gz" "http://127.0.0.1:9000/devstoreaccount1/backups/backup.tar.gz"

# 2. Copy a whole directory
azcopy copy "./build/" "http://127.0.0.1:9000/devstoreaccount1/assets/" --recursive

# 3. List blobs in container
azcopy list "http://127.0.0.1:9000/devstoreaccount1/backups"
```

---

### Azure CLI (`az storage`)

```sh
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:9000/devstoreaccount1;"

# 1. Create container
az storage container create --name logs

# 2. Upload a blob
az storage blob upload --container-name logs --file ./app.log --name 2026-10-02.log

# 3. List blobs
az storage blob list --container-name logs --output table

# 4. Download a blob
az storage blob download --container-name logs --name 2026-10-02.log --file ./downloaded.log
```

---

### Azure Storage Explorer

To connect using Azure Storage Explorer:
1. Open Azure Storage Explorer and click the **Connect** icon (plug icon).
2. Choose **Storage account or service** -> **Connection string (Key or SAS)**.
3. Paste the connection string:
   ```
   DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:9000/devstoreaccount1;
   ```
4. Set Display Name to `Vessel3 (Local)` and click **Connect**.
5. You can now browse containers, upload/download files, inspect metadata, and manage blobs directly in the GUI.
