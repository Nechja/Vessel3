using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Protocols.Oci;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Oidc;
using Vessel3.Server.Pipeline;
using Vessel3.Storage;
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
        string? secretKey = null,
        OidcOptions? oidc = null,
        Action<IServiceCollection>? configureServices = null)
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
            Oidc: oidc,
            ContainerReposEnabled: true);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);
        configureServices?.Invoke(builder.Services);

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

        var pingRes = await client.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, pingRes.StatusCode);
        Assert.True(pingRes.Headers.Contains("Www-Authenticate"));

        var tokenReq = new HttpRequestMessage(HttpMethod.Get, "/v2/token?service=127.0.0.1&scope=repository:test-app:pull,push");
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}"));
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        var tokenRes = await client.SendAsync(tokenReq);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);

        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(tokenJson);
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

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

        var startRes = await client.PostAsync("/v2/my-app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
        Assert.True(startRes.Headers.Contains("Location"));
        var uploadLocation = startRes.Headers.GetValues("Location").First();

        var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadLocation)
        {
            Content = new ByteArrayContent(layerBytes)
        };
        patchReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var patchRes = await client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.Accepted, patchRes.StatusCode);

        var putRes = await client.PutAsync($"{uploadLocation}?digest={layerDigest}", null);
        Assert.Equal(HttpStatusCode.Created, putRes.StatusCode);
        Assert.Equal($"/v2/my-app/blobs/{layerDigest}", putRes.Headers.GetValues("Location").First());

        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/my-app/blobs/{layerDigest}"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
        Assert.Equal(layerBytes.Length, headRes.Content.Headers.ContentLength);
        Assert.Equal(layerDigest, headRes.Headers.GetValues("Docker-Content-Digest").First());

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

        var postReq = new HttpRequestMessage(HttpMethod.Post, $"/v2/fast-app/blobs/uploads/?digest={layerDigest}")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        var postRes = await client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);

        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/fast-app/blobs/{layerDigest}"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);

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

        var layerBytes = Encoding.UTF8.GetBytes("app-layer-content");
        var layerSha = Convert.ToHexStringLower(SHA256.HashData(layerBytes));
        var layerDigest = $"sha256:{layerSha}";
        var uploadPost = new HttpRequestMessage(HttpMethod.Post, $"/v2/web-service/blobs/uploads/?digest={layerDigest}")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        var uploadRes = await client.SendAsync(uploadPost);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);

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

        var headByTag = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/web-service/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.OK, headByTag.StatusCode);

        var headByDigest = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/web-service/manifests/{manifestDigest}"));
        Assert.Equal(HttpStatusCode.OK, headByDigest.StatusCode);

        var getManifest = await client.GetAsync("/v2/web-service/manifests/v1.0.0");
        Assert.Equal(HttpStatusCode.OK, getManifest.StatusCode);
        var readJson = await getManifest.Content.ReadAsStringAsync();
        Assert.Equal(manifestJson, readJson);

        var tagsRes = await client.GetAsync("/v2/web-service/tags/list");
        Assert.Equal(HttpStatusCode.OK, tagsRes.StatusCode);
        var tagsJson = await tagsRes.Content.ReadAsStringAsync();
        using var tagsDoc = JsonDocument.Parse(tagsJson);
        Assert.Equal("web-service", tagsDoc.RootElement.GetProperty("name").GetString());
        var tagsArray = tagsDoc.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("v1.0.0", tagsArray);

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

        var missingBlobRes = await client.GetAsync("/v2/ghost-app/blobs/sha256:1111111111111111111111111111111111111111111111111111111111111111");
        Assert.Equal(HttpStatusCode.NotFound, missingBlobRes.StatusCode);
        var missingBlobJson = await missingBlobRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(missingBlobJson))
        {
            var code = doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
            Assert.Equal("BLOB_UNKNOWN", code);
        }

        var missingTagsRes = await client.GetAsync("/v2/non-existent-repo/tags/list");
        Assert.Equal(HttpStatusCode.NotFound, missingTagsRes.StatusCode);
        var missingTagsJson = await missingTagsRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(missingTagsJson))
        {
            var code = doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
            Assert.Equal("NAME_UNKNOWN", code);
        }

        var startRes = await client.PostAsync("/v2/cancel-app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
        var uploadLocation = startRes.Headers.GetValues("Location").First();

        var cancelRes = await client.DeleteAsync(uploadLocation);
        Assert.Equal(HttpStatusCode.NoContent, cancelRes.StatusCode);

        var getCanceledRes = await client.GetAsync(uploadLocation);
        Assert.Equal(HttpStatusCode.NotFound, getCanceledRes.StatusCode);

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

        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/delete-repo/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);

        var delRes = await client.DeleteAsync("/v2/delete-repo/manifests/v1.0.0");
        Assert.Equal(HttpStatusCode.Accepted, delRes.StatusCode);

        var headGoneRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/v2/delete-repo/manifests/v1.0.0"));
        Assert.Equal(HttpStatusCode.NotFound, headGoneRes.StatusCode);
    }

    [Fact]
    public async Task OidcBearerToken_CatalogAndManifestAccess_And_VesselClient_Works()
    {
        var fakeVerifier = new FakeVerifier(new Dictionary<string, (string Subject, bool IsAdmin)>
        {
            ["drummer.jwt.token"] = ("drummer", false)
        });
        var oidcOptions = new OidcOptions("https://issuer.example.com", "vessel3-client", "vessel3-client", null);

        var (app, client) = await StartServer(
            "oidc-con-repo",
            accessKey: "root",
            secretKey: "rootpass",
            oidc: oidcOptions,
            configureServices: s => s.AddSingleton<ITokenVerifier>(fakeVerifier));

        var req = new HttpRequestMessage(HttpMethod.Get, "/v2/_catalog");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "drummer.jwt.token");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var layerBytes = Encoding.UTF8.GetBytes("drummer-layer-bytes");
        var layerDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(layerBytes))}";
        var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"/v2/rainier/blobs/uploads/?digest={layerDigest}")
        {
            Content = new ByteArrayContent(layerBytes)
        };
        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "drummer.jwt.token");
        var uploadRes = await client.SendAsync(uploadReq);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);

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
        var putReq = new HttpRequestMessage(HttpMethod.Put, "/v2/rainier/manifests/v1.0.0")
        {
            Content = new StringContent(manifestJson, Encoding.UTF8, "application/vnd.docker.distribution.manifest.v2+json")
        };
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "drummer.jwt.token");
        var putRes = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.Created, putRes.StatusCode);

        var vesselClient = new VesselClient(client, new VesselClientOptions(app.Urls.First(), null, null, "drummer.jwt.token"));
        var reposResult = await vesselClient.ListContainerReposAsync();
        Assert.True(reposResult.TryGetValue(out var repos, out var reposErr), reposErr?.Message);
        Assert.Contains("rainier", repos);

        var tagsResult = await vesselClient.ListContainerTagsAsync("rainier");
        Assert.True(tagsResult.TryGetValue(out var tags, out var tagsErr), tagsErr?.Message);
        Assert.Contains("v1.0.0", tags);

        var badVesselClient = new VesselClient(client, new VesselClientOptions(app.Urls.First(), null, null, "invalid.jwt.token"));
        var badResult = await badVesselClient.ListContainerReposAsync();
        Assert.False(badResult.TryGetValue(out _, out var badErr));
        Assert.Equal(401, badErr.Status);
        Assert.Equal("UNAUTHORIZED", badErr.Code);
    }

    [Fact]
    public async Task OidcBearerToken_ReadOnlyUser_CannotWriteRepositories()
    {
        var fakeVerifier = new FakeVerifier(new Dictionary<string, (string Subject, bool IsAdmin)>
        {
            ["bobby.jwt.token"] = ("bobby", false)
        });
        var oidcOptions = new OidcOptions("https://issuer.example.com", "vessel3-client", "vessel3-client", null);

        var (app, client) = await StartServer(
            "oidc-ro-repo",
            accessKey: "root",
            secretKey: "rootpass",
            oidc: oidcOptions,
            configureServices: s => s.AddSingleton<ITokenVerifier>(fakeVerifier));

        var identity = app.Services.GetRequiredService<IIdentityRegistry>();
        var userRes = identity.CreateUser("bobby", UserRole.ReadOnly);
        Assert.True(userRes.TryGetValue(out _, out _));

        var getReq = new HttpRequestMessage(HttpMethod.Get, "/v2/_catalog");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bobby.jwt.token");
        var getRes = await client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var postReq = new HttpRequestMessage(HttpMethod.Post, "/v2/hood/blobs/uploads/");
        postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bobby.jwt.token");
        var postRes = await client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Forbidden, postRes.StatusCode);

        var postJson = await postRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(postJson);
        var errCode = doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
        Assert.Equal("DENIED", errCode);
    }

    private sealed class FakeVerifier(Dictionary<string, (string Subject, bool IsAdmin)> tokens) : ITokenVerifier
    {
        public Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct) =>
            Task.FromResult(tokens.TryGetValue(token, out var info)
                ? (Result<VerifiedIdentity>)new VerifiedIdentity(info.Subject, ["vessel3-client"], info.IsAdmin)
                : new InvalidTokenError("token signature invalid"));
    }
}
