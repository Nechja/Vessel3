using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Vessel3.Protocols.Oci;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public class OciProtocolWireTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<HttpClient> httpClients = [];

    public OciProtocolWireTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-oci-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in httpClients)
        {
            client.Dispose();
        }

        foreach (var app in runningApps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<(WebApplication App, HttpClient Client)> StartServer(
        string subDir,
        string? accessKey = null,
        string? secretKey = null)
    {
        var dataDir = Path.Combine(testDir, subDir);
        Directory.CreateDirectory(dataDir);

        var config = new VesselConfig(
            DataRoot: dataDir,
            AccessKey: accessKey,
            SecretKey: secretKey,
            Region: "us-east-1",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(120),
            LifecycleInterval: TimeSpan.FromHours(1),
            CompactInterval: TimeSpan.FromHours(1),
            CompactThresholdBytes: 64 * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(1),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null,
            ContainerReposEnabled: true);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var url = app.Urls.First();
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(url) };
        httpClients.Add(client);

        return (app, client);
    }

    [Fact]
    public async Task Ping_Unauthenticated_Returns_200()
    {
        var (_, client) = await StartServer("unauth");

        var response = await client.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-API-Version"));
        Assert.Equal("registry/2.0", response.Headers.GetValues("Docker-Distribution-API-Version").First());
    }

    [Fact]
    public async Task Ping_Authenticated_Challenges_And_Token_Exchange_Works()
    {
        const string key = "TESTKEY123";
        const string secret = "TESTSECRET456";
        var (_, client) = await StartServer("auth", key, secret);

        // 1. Initial unauthenticated ping -> 401 Challenge
        var pingRes = await client.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, pingRes.StatusCode);
        Assert.True(pingRes.Headers.Contains("Www-Authenticate"));

        // 2. Request token with Basic Auth
        var tokenReq = new HttpRequestMessage(HttpMethod.Get, "/v2/token?service=127.0.0.1&scope=repository:test-app:pull,push");
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}"));
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        var tokenRes = await client.SendAsync(tokenReq);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);

        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(tokenJson);
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // 3. Ping with Bearer token -> 200 OK
        var authPingReq = new HttpRequestMessage(HttpMethod.Get, "/v2/");
        authPingReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var authPingRes = await client.SendAsync(authPingReq);
        Assert.Equal(HttpStatusCode.OK, authPingRes.StatusCode);
    }

    [Fact]
    public async Task Blob_Chunked_Upload_And_Download_Flow()
    {
        var (_, client) = await StartServer("blobs-chunked");
        var layerBytes = Encoding.UTF8.GetBytes("hello-container-world-layer-bytes");
        var layerSha = Convert.ToHexStringLower(SHA256.HashData(layerBytes));
        var layerDigest = $"sha256:{layerSha}";

        // 1. Start chunked upload
        var startRes = await client.PostAsync("/v2/my-app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
        Assert.True(startRes.Headers.Contains("Location"));
        var uploadLocation = startRes.Headers.GetValues("Location").First();

        // 2. Upload chunk via PATCH
        var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadLocation)
        {
            Content = new ByteArrayContent(layerBytes)
        };
        patchReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var patchRes = await client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.Accepted, patchRes.StatusCode);

        // 3. Commit upload via PUT with ?digest=
        var putRes = await client.PutAsync($"{uploadLocation}?digest={layerDigest}", null);
        Assert.Equal(HttpStatusCode.Created, putRes.StatusCode);
        Assert.Equal($"/v2/my-app/blobs/{layerDigest}", putRes.Headers.GetValues("Location").First());

        // 4. HEAD blob
        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/my-app/blobs/{layerDigest}"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
        Assert.Equal(layerBytes.Length, headRes.Content.Headers.ContentLength);
        Assert.Equal(layerDigest, headRes.Headers.GetValues("Docker-Content-Digest").First());

        // 5. GET blob
        var getRes = await client.GetAsync($"/v2/my-app/blobs/{layerDigest}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var downloadedBytes = await getRes.Content.ReadAsByteArrayAsync();
        Assert.Equal(layerBytes, downloadedBytes);
    }

    [Fact]
    public async Task Monolithic_Blob_Upload_And_Bad_Digest_Rejection()
    {
        var (_, client) = await StartServer("blobs-monolithic");
        var layerBytes = Encoding.UTF8.GetBytes("monolithic-blob-bytes");
        var layerSha = Convert.ToHexStringLower(SHA256.HashData(layerBytes));
        var layerDigest = $"sha256:{layerSha}";

        // Monolithic POST with ?digest=
        var postReq = new HttpRequestMessage(HttpMethod.Post, $"/v2/fast-app/blobs/uploads/?digest={layerDigest}")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        var postRes = await client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);

        // Verify it exists
        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/fast-app/blobs/{layerDigest}"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);

        // Bad digest mismatch rejection
        var badPostReq = new HttpRequestMessage(HttpMethod.Post, "/v2/fast-app/blobs/uploads/?digest=sha256:0000000000000000000000000000000000000000000000000000000000000000")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        var badRes = await client.SendAsync(badPostReq);
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);
    }

    [Fact]
    public async Task Manifest_Push_Get_And_Tags_And_Catalog()
    {
        var (_, client) = await StartServer("manifest-flow");

        // 1. Upload a layer first
        var layerBytes = Encoding.UTF8.GetBytes("app-layer-content");
        var layerSha = Convert.ToHexStringLower(SHA256.HashData(layerBytes));
        var layerDigest = $"sha256:{layerSha}";
        var uploadPost = new HttpRequestMessage(HttpMethod.Post, $"/v2/web-service/blobs/uploads/?digest={layerDigest}")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        var uploadRes = await client.SendAsync(uploadPost);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);

        // 2. Upload manifest for tag v1.0.0
        var manifestJson = $$"""
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
            "config": {
                "mediaType": "application/vnd.docker.container.image.v1+json",
                "size": {{layerBytes.Length}},
                "digest": "{{layerDigest}}"
            },
            "layers": [
                {
                    "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
                    "size": {{layerBytes.Length}},
                    "digest": "{{layerDigest}}"
                }
            ]
        }
        """;

        var manifestContent = new StringContent(manifestJson, Encoding.UTF8, "application/vnd.docker.distribution.manifest.v2+json");
        var putManifestRes = await client.PutAsync("/v2/web-service/manifests/v1.0.0", manifestContent);
        Assert.Equal(HttpStatusCode.Created, putManifestRes.StatusCode);
        Assert.True(putManifestRes.Headers.Contains("Docker-Content-Digest"));
        var manifestDigest = putManifestRes.Headers.GetValues("Docker-Content-Digest").First();

        // 3. HEAD manifest by tag and by digest
        var headByTag = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/web-service/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.OK, headByTag.StatusCode);

        var headByDigest = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/web-service/manifests/{manifestDigest}"));
        Assert.Equal(HttpStatusCode.OK, headByDigest.StatusCode);

        // 4. GET manifest
        var getManifest = await client.GetAsync("/v2/web-service/manifests/v1.0.0");
        Assert.Equal(HttpStatusCode.OK, getManifest.StatusCode);
        var readJson = await getManifest.Content.ReadAsStringAsync();
        Assert.Equal(manifestJson, readJson);

        // 5. GET tags list
        var tagsRes = await client.GetAsync("/v2/web-service/tags/list");
        Assert.Equal(HttpStatusCode.OK, tagsRes.StatusCode);
        var tagsJson = await tagsRes.Content.ReadAsStringAsync();
        using var tagsDoc = JsonDocument.Parse(tagsJson);
        Assert.Equal("web-service", tagsDoc.RootElement.GetProperty("name").GetString());
        var tagsArray = tagsDoc.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("v1.0.0", tagsArray);

        // 6. GET _catalog
        var catRes = await client.GetAsync("/v2/_catalog");
        Assert.Equal(HttpStatusCode.OK, catRes.StatusCode);
        var catJson = await catRes.Content.ReadAsStringAsync();
        using var catDoc = JsonDocument.Parse(catJson);
        var reposArray = catDoc.RootElement.GetProperty("repositories").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("web-service", reposArray);
    }

    [Fact]
    public async Task Manifest_Delete_And_Upload_Cancel_And_404_Errors()
    {
        var (_, client) = await StartServer("deletes-and-errors");

        // 1. Unknown blob returns 404 with BLOB_UNKNOWN
        var missingBlobRes = await client.GetAsync("/v2/ghost-app/blobs/sha256:1111111111111111111111111111111111111111111111111111111111111111");
        Assert.Equal(HttpStatusCode.NotFound, missingBlobRes.StatusCode);
        var missingBlobJson = await missingBlobRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(missingBlobJson))
        {
            var code = doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
            Assert.Equal("BLOB_UNKNOWN", code);
        }

        // 2. Unknown tags returns 404 with NAME_UNKNOWN
        var missingTagsRes = await client.GetAsync("/v2/non-existent-repo/tags/list");
        Assert.Equal(HttpStatusCode.NotFound, missingTagsRes.StatusCode);
        var missingTagsJson = await missingTagsRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(missingTagsJson))
        {
            var code = doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
            Assert.Equal("NAME_UNKNOWN", code);
        }

        // 3. Upload cancel flow
        var startRes = await client.PostAsync("/v2/cancel-app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
        var uploadLocation = startRes.Headers.GetValues("Location").First();

        var cancelRes = await client.DeleteAsync(uploadLocation);
        Assert.Equal(HttpStatusCode.NoContent, cancelRes.StatusCode);

        // Fetching canceled upload returns 404
        var getCanceledRes = await client.GetAsync(uploadLocation);
        Assert.Equal(HttpStatusCode.NotFound, getCanceledRes.StatusCode);

        // 4. Manifest push and delete
        var layerBytes = Encoding.UTF8.GetBytes("layer-to-delete");
        var layerDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(layerBytes))}";
        var upRes = await client.PostAsync($"/v2/delete-repo/blobs/uploads/?digest={layerDigest}", new ByteArrayContent(layerBytes));
        Assert.Equal(HttpStatusCode.Created, upRes.StatusCode);

        var manifestJson = $$"""
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
            "layers": [
                {
                    "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
                    "size": {{layerBytes.Length}},
                    "digest": "{{layerDigest}}"
                }
            ]
        }
        """;
        var putRes = await client.PutAsync("/v2/delete-repo/manifests/v1.0.0", new StringContent(manifestJson, Encoding.UTF8, "application/vnd.docker.distribution.manifest.v2+json"));
        Assert.Equal(HttpStatusCode.Created, putRes.StatusCode);

        // Verify it exists
        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/delete-repo/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);

        // Delete manifest by tag
        var delRes = await client.DeleteAsync("/v2/delete-repo/manifests/v1.0.0");
        Assert.Equal(HttpStatusCode.Accepted, delRes.StatusCode);

        // Verify it is gone
        var headGoneRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/delete-repo/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.NotFound, headGoneRes.StatusCode);
    }
}
