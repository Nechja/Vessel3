using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Oidc;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public class NativeClientTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<IDisposable> disposables = [];

    public NativeClientTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-native-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in disposables)
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

    private async Task<(WebApplication App, IVesselClient Client)> StartServer(
        string subDir,
        string? accessKey = null,
        string? secretKey = null,
        string? clientAccessKey = null,
        string? clientSecretKey = null,
        string? clientBearerToken = null,
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
            Oidc: oidc);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var client = CreateClient(app, clientAccessKey ?? accessKey, clientSecretKey ?? secretKey, clientBearerToken);
        return (app, client);
    }

    private IVesselClient CreateClient(
        WebApplication app,
        string? accessKey = null,
        string? secretKey = null,
        string? bearerToken = null)
    {
        var client = new VesselClient(app.Urls.First(), accessKey, secretKey, bearerToken);
        disposables.Add(client);
        return client;
    }

    [Fact]
    public async Task UnauthenticatedMode_AllowsAllOperations()
    {
        var (app, client) = await StartServer("unauth");
        var whoAmI = await client.WhoAmIAsync();
        Assert.True(whoAmI.TryGetValue(out var me, out var err), $"Error was: {err?.Code} - {err?.Message} (Status {err?.Status})");
        Assert.Equal("system", me.Username);
        Assert.Equal("Admin", me.Role);

        var createBucket = await client.CreateBucketAsync("test-bucket");
        Assert.True(createBucket is Result.OkResult);

        var listBuckets = await client.ListBucketsAsync();
        Assert.True(listBuckets.TryGetValue(out var buckets, out _));
        Assert.Contains(buckets, b => b.Name == "test-bucket");

        var payload = "Hello Vessel3 Native Protocol!"u8.ToArray();
        using var putStream = new MemoryStream(payload);
        var metadata = new Dictionary<string, string> { ["author"] = "tester" };
        var putResult = await client.PutObjectAsync("test-bucket", "notes/hello.txt", putStream, "text/plain", metadata);
        Assert.True(putResult.TryGetValue(out var putOutcome, out _));
        Assert.False(string.IsNullOrEmpty(putOutcome.ETag));
        Assert.Equal(payload.Length, putOutcome.Size);

        var statResult = await client.StatObjectAsync("test-bucket", "notes/hello.txt");
        Assert.True(statResult.TryGetValue(out var stat, out _));
        Assert.Equal(payload.Length, stat.Size);
        Assert.Equal(putOutcome.ETag, stat.ETag);

        var getResult = await client.GetObjectAsync("test-bucket", "notes/hello.txt");
        Assert.True(getResult.TryGetValue(out var download, out _));
        using (download)
        {
            Assert.Equal("text/plain", download.ContentType);
            Assert.Equal(payload.Length, download.ContentLength);
            Assert.Equal(putOutcome.ETag, download.ETag);
            Assert.Equal("tester", download.Metadata["author"]);

            using var mem = new MemoryStream();
            await download.Content.CopyToAsync(mem);
            Assert.Equal(payload, mem.ToArray());
        }

        var listObjects = await client.ListObjectsAsync("test-bucket", prefix: "notes/");
        Assert.True(listObjects.TryGetValue(out var page, out _));
        Assert.Single(page.Objects);
        Assert.Equal("notes/hello.txt", page.Objects[0].Key);

        var delObj = await client.DeleteObjectAsync("test-bucket", "notes/hello.txt");
        Assert.True(delObj is Result.OkResult);

        var delBucket = await client.DeleteBucketAsync("test-bucket");
        Assert.True(delBucket is Result.OkResult);
    }

    [Fact]
    public async Task SoloAuthenticatedMode_EnforcesAuthentication()
    {
        var (app, adminClient) = await StartServer("solo-auth", accessKey: "admin-key", secretKey: "admin-secret-pass");

        var whoAmI = await adminClient.WhoAmIAsync();
        Assert.True(whoAmI.TryGetValue(out var me, out _));
        Assert.Equal("admin", me.Username);
        Assert.Equal("Admin", me.Role);

        var anonClient = CreateClient(app);

        var anonWhoAmI = await anonClient.WhoAmIAsync();
        Assert.True(anonWhoAmI is Result<WhoAmIDto>.Failure);

        var anonList = await anonClient.ListBucketsAsync();
        Assert.False(anonList.TryGetValue(out _, out var anonErr));
        Assert.Equal(401, anonErr.Status);

        var wrongClient = CreateClient(app, "admin-key", "wrong-secret");

        var wrongList = await wrongClient.ListBucketsAsync();
        Assert.False(wrongList.TryGetValue(out _, out var wrongErr));
        Assert.Equal(401, wrongErr.Status);
    }

    [Fact]
    public async Task MultiUserMode_EnforcesBucketIsolationAndCapabilities()
    {
        var (app, adminClient) = await StartServer("multi-user", accessKey: "root-key", secretKey: "root-secret-pass");

        var aliceUserRes = await adminClient.CreateUserAsync("alice", "Member");
        Assert.True(aliceUserRes.TryGetValue(out var aliceUser, out _));

        var bobUserRes = await adminClient.CreateUserAsync("bob", "Member");
        Assert.True(bobUserRes.TryGetValue(out var bobUser, out _));

        var aliceKeyRes = await adminClient.CreateAccessKeyAsync(aliceUser.Id, description: "Alice key");
        Assert.True(aliceKeyRes.TryGetValue(out var aliceKey, out _));

        var bobKeyRes = await adminClient.CreateAccessKeyAsync(bobUser.Id, description: "Bob key");
        Assert.True(bobKeyRes.TryGetValue(out var bobKey, out _));

        var aliceClient = CreateClient(app, aliceKey.Id, aliceKey.SecretKey);
        var bobClient = CreateClient(app, bobKey.Id, bobKey.SecretKey);

        var aliceWhoAmI = await aliceClient.WhoAmIAsync();
        Assert.True(aliceWhoAmI.TryGetValue(out var aliceWho, out _));
        Assert.Equal("alice", aliceWho.Username);
        Assert.Equal("Member", aliceWho.Role);

        var bobWhoAmI = await bobClient.WhoAmIAsync();
        Assert.True(bobWhoAmI.TryGetValue(out var bobWho, out _));
        Assert.Equal("bob", bobWho.Username);
        Assert.Equal("Member", bobWho.Role);

        var createAliceBucket = await aliceClient.CreateBucketAsync("alice-vault");
        Assert.True(createAliceBucket is Result.OkResult);

        var createBobBucket = await bobClient.CreateBucketAsync("bob-vault");
        Assert.True(createBobBucket is Result.OkResult);

        var aliceList = await aliceClient.ListBucketsAsync();
        Assert.True(aliceList.TryGetValue(out var aliceBuckets, out _));
        Assert.Single(aliceBuckets);
        Assert.Equal("alice-vault", aliceBuckets[0].Name);

        var bobList = await bobClient.ListBucketsAsync();
        Assert.True(bobList.TryGetValue(out var bobBuckets, out _));
        Assert.Single(bobBuckets);
        Assert.Equal("bob-vault", bobBuckets[0].Name);

        var adminList = await adminClient.ListBucketsAsync();
        Assert.True(adminList.TryGetValue(out var allBuckets, out _));
        Assert.True(allBuckets.Count >= 2);

        using var payload = new MemoryStream("Alice's Secret Data"u8.ToArray());
        var putRes = await aliceClient.PutObjectAsync("alice-vault", "diary.txt", payload, "text/plain");
        Assert.True(putRes.TryGetValue(out _, out _));

        var bobReadFail = await bobClient.GetObjectAsync("alice-vault", "diary.txt");
        Assert.False(bobReadFail.TryGetValue(out _, out var bobErr));
        Assert.Equal(403, bobErr.Status);

        using var bobPayload = new MemoryStream("Bob intruder"u8.ToArray());
        var bobWriteFail = await bobClient.PutObjectAsync("alice-vault", "intruder.txt", bobPayload);
        Assert.False(bobWriteFail.TryGetValue(out _, out var bobWriteErr));
        Assert.Equal(403, bobWriteErr.Status);

        var bobDeleteFail = await bobClient.DeleteBucketAsync("alice-vault");
        Assert.True(bobDeleteFail.TryGetError(out var bobDelErr));
        Assert.Equal(403, bobDelErr.Status);

        var setPublicRead = await aliceClient.SetBucketAccessAsync("alice-vault", new BucketAccessDto(PublicRead: true, ReadOnly: false));
        Assert.True(setPublicRead is Result.OkResult);

        var anonClient = CreateClient(app);

        var anonGet = await anonClient.GetObjectAsync("alice-vault", "diary.txt");
        Assert.True(anonGet.TryGetValue(out var anonDownload, out _));
        using (anonDownload)
        {
            using var reader = new StreamReader(anonDownload.Content, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            Assert.Equal("Alice's Secret Data", text);
        }

        using var anonPayload = new MemoryStream("Anon denied"u8.ToArray());
        var anonWriteFail = await anonClient.PutObjectAsync("alice-vault", "anon.txt", anonPayload);
        Assert.False(anonWriteFail.TryGetValue(out _, out var anonWriteErr));
        Assert.Equal(401, anonWriteErr.Status);

        var setVersion = await aliceClient.SetBucketVersioningAsync("alice-vault", "Enabled");
        Assert.True(setVersion is Result.OkResult);

        var getVersion = await aliceClient.GetBucketVersioningAsync("alice-vault");
        Assert.True(getVersion.TryGetValue(out var vDto, out _));
        Assert.Equal("Enabled", vDto.Status);

        var aliceGcFail = await aliceClient.RunGcAsync();
        Assert.False(aliceGcFail.TryGetValue(out _, out var aliceGcErr));
        Assert.Equal(403, aliceGcErr.Status);

        var adminGc = await adminClient.RunGcAsync();
        Assert.True(adminGc.TryGetValue(out _, out _));

        var adminSweep = await adminClient.RunSweepAsync();
        Assert.True(adminSweep.TryGetValue(out _, out _));

        var revokeRes = await adminClient.RevokeAccessKeyAsync(aliceKey.Id);
        Assert.True(revokeRes is Result.OkResult);

        var aliceRevokedCall = await aliceClient.ListBucketsAsync();
        Assert.False(aliceRevokedCall.TryGetValue(out _, out var revokedErr));
        Assert.Equal(401, revokedErr.Status);
    }

    private sealed class FakeVerifier : ITokenVerifier
    {
        public Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct) =>
            Task.FromResult(token == "valid.jwt.token"
                ? (Result<VerifiedIdentity>)new VerifiedIdentity("oidc-alice", ["vessel3-client"])
                : new InvalidTokenError("token signature invalid"));
    }

    [Fact]
    public async Task OidcBearerToken_AuthenticatesAndJitProvisionsUser()
    {
        var oidcOptions = new OidcOptions("https://issuer.example.com", "vessel3-client", "vessel3-client", null);
        var fakeVerifier = new FakeVerifier();

        var (app, client) = await StartServer(
            "oidc-native",
            accessKey: "root",
            secretKey: "rootpass",
            clientBearerToken: "valid.jwt.token",
            oidc: oidcOptions,
            configureServices: services =>
            {
                services.AddSingleton<ITokenVerifier>(fakeVerifier);
            });

        var whoAmI = await client.WhoAmIAsync();
        Assert.True(whoAmI.TryGetValue(out var me, out _));
        Assert.Equal("oidc-alice", me.Username);
        Assert.Equal("Member", me.Role);

        var createBucket = await client.CreateBucketAsync("alice-oidc-bucket");
        Assert.True(createBucket is Result.OkResult);

        var listBuckets = await client.ListBucketsAsync();
        Assert.True(listBuckets.TryGetValue(out var buckets, out _));
        Assert.Single(buckets);
        Assert.Equal("alice-oidc-bucket", buckets[0].Name);

        var badClient = CreateClient(app, bearerToken: "invalid.token");
        var badWhoAmI = await badClient.WhoAmIAsync();
        Assert.False(badWhoAmI.TryGetValue(out _, out var badErr));
        Assert.Equal(401, badErr.Status);

        var anonClient = CreateClient(app);
        var anonWhoAmI = await anonClient.WhoAmIAsync();
        Assert.False(anonWhoAmI.TryGetValue(out _, out var anonErr));
        Assert.Equal(401, anonErr.Status);
    }

    [Fact]
    public async Task UserRoleManagement_PromoteAndSuspend()
    {
        var (app, adminClient) = await StartServer(
            "role-mgmt",
            accessKey: "root",
            secretKey: "rootpass",
            clientAccessKey: "root",
            clientSecretKey: "rootpass");

        var createAlice = await adminClient.CreateUserAsync("alice-role-test", "Member");
        Assert.True(createAlice.TryGetValue(out var alice, out _));
        Assert.Equal("Member", alice.Role);

        var aliceKeyRes = await adminClient.CreateAccessKeyAsync(alice.Id);
        Assert.True(aliceKeyRes.TryGetValue(out var aliceKey, out _));

        var aliceClient = CreateClient(app, accessKey: aliceKey.Id, secretKey: aliceKey.SecretKey);

        var aliceGcDenied = await aliceClient.RunGcAsync();
        Assert.False(aliceGcDenied.TryGetValue(out _, out var gcErr));
        Assert.Equal(403, gcErr.Status);

        var aliceSelfPromoteDenied = await aliceClient.UpdateUserRoleAsync(alice.Id, "Admin");
        Assert.True(aliceSelfPromoteDenied.TryGetError(out var selfErr));
        Assert.Equal(403, selfErr.Status);

        var promoteRes = await adminClient.UpdateUserRoleAsync(alice.Id, "Admin");
        Assert.True(promoteRes is Result.OkResult);

        var aliceWhoAmI = await aliceClient.WhoAmIAsync();
        Assert.True(aliceWhoAmI.TryGetValue(out var aliceMe, out _));
        Assert.Equal("Admin", aliceMe.Role);

        var aliceGcAllowed = await aliceClient.RunGcAsync();
        Assert.True(aliceGcAllowed.TryGetValue(out _, out _));

        var suspendRes = await adminClient.UpdateUserStatusAsync(alice.Id, "Suspended");
        Assert.True(suspendRes is Result.OkResult);

        var aliceSuspendedWhoAmI = await aliceClient.WhoAmIAsync();
        Assert.False(aliceSuspendedWhoAmI.TryGetValue(out _, out var suspErr));
        Assert.Equal(401, suspErr.Status);

        var activateRes = await adminClient.UpdateUserStatusAsync(alice.Id, "Active");
        Assert.True(activateRes is Result.OkResult);

        var aliceActiveWhoAmI = await aliceClient.WhoAmIAsync();
        Assert.True(aliceActiveWhoAmI.TryGetValue(out var activeMe, out _));
        Assert.Equal("Active", (await adminClient.ListUsersAsync()).Match(u => u.First(x => x.Id == alice.Id).Status, _ => ""));
    }

    [Fact]
    public async Task OidcBearerToken_AdminPromotion()
    {
        var oidcOptions = new OidcOptions("https://issuer.example.com", "vessel3-client", "vessel3-client", null);
        var fakeVerifier = new FakeAdminVerifier();

        var (app, client) = await StartServer(
            "oidc-admin",
            accessKey: "root",
            secretKey: "rootpass",
            clientBearerToken: "admin.jwt.token",
            oidc: oidcOptions,
            configureServices: services =>
            {
                services.AddSingleton<ITokenVerifier>(fakeVerifier);
            });

        var whoAmI = await client.WhoAmIAsync();
        Assert.True(whoAmI.TryGetValue(out var me, out _));
        Assert.Equal("acct_kayla.dIftEd_eU48bcFmhcaiAJA", me.Username);
        Assert.Equal("Admin", me.Role);

        var gcAllowed = await client.RunGcAsync();
        Assert.True(gcAllowed.TryGetValue(out _, out _));
    }

    private sealed class FakeAdminVerifier : ITokenVerifier
    {
        public Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct) =>
            Task.FromResult(token == "admin.jwt.token"
                ? (Result<VerifiedIdentity>)new VerifiedIdentity("acct_kayla.dIftEd_eU48bcFmhcaiAJA", ["vessel3-client"], IsAdmin: true)
                : new InvalidTokenError("token signature invalid"));
    }

    [Fact]
    public async Task ContainerRepos_ClientMethods_ListAndTagsAndDelete()
    {
        var (app, client) = await StartServer(
            "client-container-repos",
            accessKey: "root-key",
            secretKey: "root-secret",
            clientAccessKey: "root-key",
            clientSecretKey: "root-secret");

        // 1. Initial list of repos is empty
        var initialRepos = await client.ListContainerReposAsync();
        Assert.True(initialRepos.TryGetValue(out var emptyList, out _));
        Assert.Empty(emptyList);

        // 2. Put a manifest via OCI catalog directly
        var catalog = app.Services.GetRequiredService<Vessel3.Protocols.Oci.IContainerRepoCatalog>();
        var manifestBytes = Encoding.UTF8.GetBytes("""
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
            "layers": []
        }
        """);
        var putRes = catalog.PutManifest("my-service", "v1.0.0", "application/vnd.docker.distribution.manifest.v2+json", manifestBytes, []);
        Assert.True(putRes.TryGetValue(out _, out _));

        // 3. ListContainerReposAsync returns the repo
        var reposRes = await client.ListContainerReposAsync();
        Assert.True(reposRes.TryGetValue(out var repos, out _));
        Assert.Contains("my-service", repos);

        // 4. ListContainerTagsAsync returns the tag
        var tagsRes = await client.ListContainerTagsAsync("my-service");
        Assert.True(tagsRes.TryGetValue(out var tags, out _));
        Assert.Contains("v1.0.0", tags);

        // 5. DeleteContainerManifestAsync deletes the tag
        var delRes = await client.DeleteContainerManifestAsync("my-service", "v1.0.0");
        Assert.True(delRes is Result.OkResult);

        // 6. ListContainerTagsAsync now has 0 tags
        var afterTagsRes = await client.ListContainerTagsAsync("my-service");
        Assert.True(afterTagsRes.TryGetValue(out var afterTags, out _));
        Assert.Empty(afterTags);
    }

    [Fact]
    public async Task Website_Endpoints_And_Direct_Serving_RoundTrip()
    {
        var (app, client) = await StartServer(
            "website-e2e",
            accessKey: "root-key",
            secretKey: "root-secret",
            clientAccessKey: "root-key",
            clientSecretKey: "root-secret");

        var createBucket = await client.CreateBucketAsync("mysite");
        Assert.True(createBucket is Result.OkResult);

        var initialWeb = await client.GetBucketWebsiteAsync("mysite");
        Assert.True(initialWeb.TryGetValue(out var initialCfg, out _));
        Assert.Null(initialCfg);

        var setWeb = await client.SetBucketWebsiteAsync("mysite", new BucketWebsiteDto("index.html", "index.html"));
        Assert.True(setWeb is Result.OkResult);

        var getWeb = await client.GetBucketWebsiteAsync("mysite");
        Assert.True(getWeb.TryGetValue(out var configured, out _));
        Assert.NotNull(configured);
        Assert.Equal("index.html", configured.Value.IndexDocument);
        Assert.Equal("index.html", configured.Value.ErrorDocument);

        using (var indexStream = new MemoryStream("<h1>Hello Vessel3 Website</h1>"u8.ToArray()))
        {
            var putIndex = await client.PutObjectAsync("mysite", "index.html", indexStream, "text/html");
            Assert.True(putIndex.TryGetValue(out _, out _));
        }

        using (var wasmStream = new MemoryStream([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]))
        {
            var putWasm = await client.PutObjectAsync("mysite", "app.wasm", wasmStream, "application/octet-stream");
            Assert.True(putWasm.TryGetValue(out _, out _));
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var serverUrl = app.Urls.First();

        using (var noRedirectHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }))
        {
            var redirectResp = await noRedirectHttp.GetAsync($"{serverUrl}/_site/mysite");
            Assert.Equal(System.Net.HttpStatusCode.Redirect, redirectResp.StatusCode);
            Assert.Equal("/_site/mysite/", redirectResp.Headers.Location?.OriginalString);
        }

        var indexResp = await http.GetAsync($"{serverUrl}/_site/mysite/");
        Assert.Equal(System.Net.HttpStatusCode.OK, indexResp.StatusCode);
        Assert.Equal("text/html", indexResp.Content.Headers.ContentType?.MediaType);
        var indexText = await indexResp.Content.ReadAsStringAsync();
        Assert.Contains("Hello Vessel3 Website", indexText);

        var wasmResp = await http.GetAsync($"{serverUrl}/_site/mysite/app.wasm");
        Assert.Equal(System.Net.HttpStatusCode.OK, wasmResp.StatusCode);
        Assert.Equal("application/wasm", wasmResp.Content.Headers.ContentType?.MediaType);

        var spaResp = await http.GetAsync($"{serverUrl}/_site/mysite/deep/client/route");
        Assert.Equal(System.Net.HttpStatusCode.OK, spaResp.StatusCode);
        Assert.Equal("text/html", spaResp.Content.Headers.ContentType?.MediaType);
        var spaText = await spaResp.Content.ReadAsStringAsync();
        Assert.Contains("Hello Vessel3 Website", spaText);

        var delWeb = await client.DeleteBucketWebsiteAsync("mysite");
        Assert.True(delWeb is Result.OkResult);

        var afterDeleteWeb = await client.GetBucketWebsiteAsync("mysite");
        Assert.True(afterDeleteWeb.TryGetValue(out var deletedCfg, out _));
        Assert.Null(deletedCfg);

        var disabledResp = await http.GetAsync($"{serverUrl}/_site/mysite/");
        Assert.False(disabledResp.IsSuccessStatusCode);
    }
}
