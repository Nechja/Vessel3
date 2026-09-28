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
}
