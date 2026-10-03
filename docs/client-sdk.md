# C# .NET Client SDK

The `Vessel3.Client` library provides a high-level, asynchronous C# client for interacting with Vessel3 over its Native REST API.

---

## Installation & Setup

Add project reference or package dependency:
```xml
<ItemGroup>
  <ProjectReference Include="..\Vessel3.Client\Vessel3.Client.csproj" />
</ItemGroup>
```

### Dependency Injection

Register `IVesselClient` in your ASP.NET Core `Program.cs`:

```csharp
using Vessel3.Client;

// With Access Key credentials:
builder.Services.AddHttpClient<IVesselClient, VesselClient>(client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:9000/");
});
builder.Services.AddSingleton(new VesselClientOptions(
    BaseUrl: "http://127.0.0.1:9000",
    AccessKey: "V3AKADMINEXAMPLE1234",
    SecretKey: "secret-key-token"));
```

### Direct Instantiation

```csharp
using Vessel3.Client;

// Using API credentials:
using var client = new VesselClient(
    baseUrl: "http://127.0.0.1:9000",
    accessKey: "V3AKADMINEXAMPLE1234",
    secretKey: "secret-key-token");

// Or using an OIDC Bearer Token:
using var oidcClient = new VesselClient(
    baseUrl: "http://127.0.0.1:9000",
    bearerToken: "eyJhbGciOi...");
```

---

## Error Handling Pattern

Every client method returns a `Result` or `Result<T>`. Results never throw HTTP exceptions on failure:

```csharp
var result = await client.WhoAmIAsync();

if (result.TryGetValue(out var caller, out var error))
{
    Console.WriteLine($"Authenticated as: {caller.Username} ({caller.Role})");
}
else
{
    Console.WriteLine($"Error [{error.Status}]: {error.Code} - {error.Message}");
}
```

---

## Code Recipes

### 1. Bucket Management

```csharp
// 1. Create a bucket
var createResult = await client.CreateBucketAsync("documents");

// 2. Configure bucket access (e.g. enable public read)
await client.SetBucketAccessAsync("documents", new BucketAccessDto(PublicRead: true, ReadOnly: false));

// 3. Enable bucket versioning
await client.SetBucketVersioningAsync("documents", "Enabled");

// 4. List all buckets visible to the caller
var listResult = await client.ListBucketsAsync();
if (listResult.TryGetValue(out var buckets, out _))
{
    foreach (var b in buckets)
    {
        Console.WriteLine($"Bucket: {b.Name}, Owner: {b.OwnerId}, Created: {b.CreatedAt}");
    }
}

// 5. Delete an empty bucket
await client.DeleteBucketAsync("documents");
```

### 2. Uploading Objects

```csharp
byte[] fileBytes = File.ReadAllBytes("contract.pdf");
using var stream = new MemoryStream(fileBytes);

var metadata = new Dictionary<string, string>
{
    ["department"] = "finance",
    ["confidential"] = "true"
};

var putResult = await client.PutObjectAsync(
    bucket: "documents",
    key: "2026/contract.pdf",
    content: stream,
    contentType: "application/pdf",
    metadata: metadata);

if (putResult.TryGetValue(out var outcome, out var err))
{
    Console.WriteLine($"Uploaded! ETag: {outcome.ETag}, VersionId: {outcome.VersionId}, SHA256: {outcome.Sha256}");
}
```

### 3. Downloading Objects

```csharp
var getResult = await client.GetObjectAsync("documents", "2026/contract.pdf");

if (getResult.TryGetValue(out var download, out var err))
{
    using (download) // VesselObjectDownload is IDisposable
    {
        Console.WriteLine($"Size: {download.ContentLength} bytes, ETag: {download.ETag}");
        Console.WriteLine($"Content-Type: {download.ContentType}");

        // Access custom metadata
        if (download.Metadata.TryGetValue("department", out var dept))
        {
            Console.WriteLine($"Department: {dept}");
        }

        // Stream object bytes to disk
        using var destination = File.Create("downloaded-contract.pdf");
        await download.Content.CopyToAsync(destination);
    }
}
```

### 4. Querying Metadata (`StatObject`)

```csharp
var statResult = await client.StatObjectAsync("documents", "2026/contract.pdf");

if (statResult.TryGetValue(out var stat, out _))
{
    Console.WriteLine($"Key: {stat.Key}, Size: {stat.Size} bytes, ETag: {stat.ETag}");
}
```

### 5. Listing Objects & Pagination

```csharp
var pageResult = await client.ListObjectsAsync(
    bucket: "documents",
    prefix: "2026/",
    delimiter: "/",
    limit: 50);

if (pageResult.TryGetValue(out var page, out _))
{
    foreach (var obj in page.Objects)
    {
        Console.WriteLine($"File: {obj.Key} ({obj.Size} bytes)");
    }

    foreach (var folder in page.Prefixes)
    {
        Console.WriteLine($"Directory: {folder}");
    }

    if (page.IsTruncated)
    {
        Console.WriteLine($"Next continuation marker: {page.NextMarker}");
    }
}
```

### 6. Managing IAM Users & Access Keys

```csharp
// 1. Create a tenant user (requires Admin role)
var userResult = await client.CreateUserAsync("bob", role: "Member");
if (userResult.TryGetValue(out var user, out _))
{
    Console.WriteLine($"Created user: {user.Username} (ID: {user.Id})");

    // 2. Issue an access key with a 30-day expiration
    var keyResult = await client.CreateAccessKeyAsync(
        userId: user.Id,
        description: "Bob Laptop Key",
        ttl: TimeSpan.FromDays(30));

    if (keyResult.TryGetValue(out var key, out _))
    {
        Console.WriteLine($"Access Key ID: {key.Id}");
        Console.WriteLine($"Secret Key:    {key.SecretKey}");
        Console.WriteLine($"Expires:       {key.ExpiresAt}");
    }

    // 3. Revoke access key
    await client.RevokeAccessKeyAsync(key.Id);

    // 4. Delete user
    await client.DeleteUserAsync(user.Id);
}
```

### 7. Administrative Maintenance Sweeps

```csharp
// Trigger unreferenced blob garbage collection (grace period: 1 hour)
var gcResult = await client.RunGcAsync(minBlobAgeSec: 3600);
if (gcResult.TryGetValue(out var gc, out _))
{
    Console.WriteLine($"Cleaned up {gc.BlobsDeleted} blobs and {gc.UploadsReaped} orphan uploads.");
}

// Trigger bucket lifecycle policy sweep
var sweepResult = await client.RunSweepAsync();
if (sweepResult.TryGetValue(out var sweep, out _))
{
    Console.WriteLine($"Pruned {sweep.Expired} expired versions and {sweep.MarkersReaped} delete markers.");
}
```

### 8. Container Repos Management

```csharp
// List all container repos
var reposResult = await client.ListContainerReposAsync();
if (reposResult.TryGetValue(out var repos, out _))
{
    foreach (var repo in repos)
    {
        Console.WriteLine($"Repository: {repo}");

        // List tags for this repository
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

// Delete a manifest by tag or digest
await client.DeleteContainerManifestAsync("my-service", "v1.0.0");
```
