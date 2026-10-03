using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure;
using Vessel3.Protocols.Azure.Auth;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Middleware;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class AzureMultiUserAuthorizationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string root;
    private readonly string identityRoot;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly TestClock clock = new(T0);
    private readonly GcGate gate = new();
    private readonly PreconditionEvaluator pre = new();
    private readonly BucketRegistry registry;
    private readonly BucketLister lister;
    private readonly IdentityRegistry identityRegistry;
    private readonly BlobPool blobs;
    private readonly ObjectStore objects;
    private readonly ChunkStager stager;
    private readonly ServiceProvider serviceProvider;

    public AzureMultiUserAuthorizationTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-azmu-{Guid.NewGuid():N}");
        identityRoot = Path.Combine(Path.GetTempPath(), $"vessel3-azmu-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(identityRoot);

        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        lister = new BucketLister(registry);
        identityRegistry = new IdentityRegistry(new IdentityOptions(identityRoot), clock);
        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        objects = new ObjectStore(registry, blobs, pre, gate);
        stager = new ChunkStager(new ChunkStagerOptions(Path.Combine(root, "uploads")), registry, blobs, durable, gate);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBucketRegistry>(registry);
        services.AddSingleton<IBucketLister>(lister);
        services.AddSingleton<IObjectStore>(objects);
        services.AddSingleton<IChunkStager>(stager);
        services.AddSingleton<IIdentityRegistry>(identityRegistry);
        services.AddVesselAzure(accessKey: null, secretKey: null);

        serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        registry.Dispose();
        identityRegistry.Dispose();
        serviceProvider.Dispose();
        CleanupDirectory(root);
        CleanupDirectory(identityRoot);
    }

    private static void CleanupDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void AzureRequestParser_RecognizesApiKeyInPathAndAuth()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "PUT";
        ctx.Request.Path = "/V3AKTESTKEY1234/my-box";
        ctx.Request.QueryString = new QueryString("?restype=container");
        ctx.Request.Headers["x-ms-version"] = "2024-11-04";
        ctx.Request.Headers.Authorization = "SharedKey V3AKTESTKEY1234:dummySig";

        Assert.True(AzureRequestParser.IsAzureRequest(ctx.Request));

        var target = AzureRequestParser.Parse(ctx.Request);
        Assert.Equal("V3AKTESTKEY1234", target.Account);
        Assert.Equal("my-box", target.Container);
        Assert.Null(target.Blob);
        Assert.Equal(AzureOperationKind.CreateContainer, target.Operation);
    }

    [Fact]
    public void AzureRequestParser_DirectRootEndpoint_ExtractsAccountFromAuth()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/my-box/blob.txt";
        ctx.Request.Headers["x-ms-version"] = "2024-11-04";
        ctx.Request.Headers.Authorization = "SharedKey V3AKTESTKEY1234:dummySig";

        Assert.True(AzureRequestParser.IsAzureRequest(ctx.Request));

        var target = AzureRequestParser.Parse(ctx.Request);
        Assert.Equal("V3AKTESTKEY1234", target.Account);
        Assert.Equal("my-box", target.Container);
        Assert.Equal("blob.txt", target.Blob);
        Assert.Equal(AzureOperationKind.GetBlob, target.Operation);
    }

    [Fact]
    public void AzureSharedKeyVerifier_AuthenticatesWithUserAccessKey()
    {
        var user = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var key = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(user.Id)).Value;

        var verifier = serviceProvider.GetRequiredService<IAzureVerifier>();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = $"/{key.Id}";
        ctx.Request.QueryString = new QueryString("?comp=list");
        ctx.Request.Headers["x-ms-version"] = "2024-11-04";
        ctx.Request.Headers["x-ms-date"] = DateTimeOffset.UtcNow.ToString("R");

        SignRequest(ctx.Request, key.Id, key.SecretKey);

        var result = verifier.Verify(ctx.Request, key.Id);
        Assert.True(result.TryGetValue(out var caller, out _));
        Assert.NotNull(caller);
        Assert.Equal(user.Id, caller.UserId);
        Assert.Equal("alice", caller.Username);
        Assert.Equal(UserRole.Member, caller.Role);
        Assert.Equal(key.Id, caller.AccessKeyId);
    }

    [Fact]
    public void AzureSharedKeyVerifier_RejectsRevokedKey()
    {
        var user = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var key = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(user.Id)).Value;
        identityRegistry.RevokeAccessKey(key.Id);

        var verifier = serviceProvider.GetRequiredService<IAzureVerifier>();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = $"/{key.Id}";
        ctx.Request.QueryString = new QueryString("?comp=list");
        ctx.Request.Headers["x-ms-version"] = "2024-11-04";
        ctx.Request.Headers["x-ms-date"] = DateTimeOffset.UtcNow.ToString("R");

        SignRequest(ctx.Request, key.Id, key.SecretKey);

        var result = verifier.Verify(ctx.Request, key.Id);
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task AzurePipeline_EnforcesTenantIsolationBetweenUsers()
    {
        var alice = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(alice.Id)).Value;

        var bob = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bob.Id)).Value;

        var middleware = serviceProvider.GetRequiredService<AzureProtocolMiddleware>();

        {
            var ctx = CreateHttpContext("PUT", $"/{aliceKey.Id}/alice-box?restype=container", aliceKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status201Created, ctx.Response.StatusCode);
        }

        {
            var ctx = CreateHttpContext("PUT", $"/{bobKey.Id}/bob-box?restype=container", bobKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status201Created, ctx.Response.StatusCode);
        }

        {
            var ctx = CreateHttpContext("GET", $"/{aliceKey.Id}?comp=list", aliceKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("<Name>alice-box</Name>", xml);
            Assert.DoesNotContain("<Name>bob-box</Name>", xml);
        }

        {
            var ctx = CreateHttpContext("GET", $"/{bobKey.Id}?comp=list", bobKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("<Name>bob-box</Name>", xml);
            Assert.DoesNotContain("<Name>alice-box</Name>", xml);
        }

        {
            var ctx = CreateHttpContext("PUT", $"/{aliceKey.Id}/alice-box/hello.txt", aliceKey, "Hello from Alice",
                h => h["x-ms-blob-type"] = "BlockBlob");
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status201Created, ctx.Response.StatusCode);
        }

        {
            var ctx = CreateHttpContext("DELETE", $"/{bobKey.Id}/alice-box?restype=container", bobKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.NotEqual(StatusCodes.Status202Accepted, ctx.Response.StatusCode);
            Assert.True(ctx.Response.StatusCode is StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound);
        }

        {
            var ctx = CreateHttpContext("GET", $"/{aliceKey.Id}/alice-box/hello.txt", aliceKey);
            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            Assert.Equal("Hello from Alice", content);
        }
    }

    private DefaultHttpContext CreateHttpContext(
        string method,
        string urlPathAndQuery,
        AccessKey key,
        string? body = null,
        Action<IHeaderDictionary>? configureHeaders = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = serviceProvider;
        ctx.Response.Body = new MemoryStream();

        var qIndex = urlPathAndQuery.IndexOf('?');
        var path = qIndex >= 0 ? urlPathAndQuery[..qIndex] : urlPathAndQuery;
        var query = qIndex >= 0 ? urlPathAndQuery[qIndex..] : "";

        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.Headers["x-ms-version"] = "2024-11-04";
        ctx.Request.Headers["x-ms-date"] = DateTimeOffset.UtcNow.ToString("R");

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.ContentType = "text/plain";
        }

        configureHeaders?.Invoke(ctx.Request.Headers);

        SignRequest(ctx.Request, key.Id, key.SecretKey);
        return ctx;
    }

    private static void SignRequest(HttpRequest req, string account, string secretKey)
    {
        var stringToSign = AzureSharedKeyVerifier.BuildStringToSign(req, account);
        byte[] keyBytes;
        try { keyBytes = Convert.FromBase64String(secretKey); }
        catch { keyBytes = Encoding.UTF8.GetBytes(secretKey); }

        using var hmac = new HMACSHA256(keyBytes);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        req.Headers.Authorization = $"SharedKey {account}:{signature}";
    }
}
