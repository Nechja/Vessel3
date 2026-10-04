using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Server;
using Vessel3.Server.S3.Bucket;
using Vessel3.Server.S3.Key;
using Xunit;

namespace Vessel3.Tests;

public class S3MultiUserAuthorizationTests : IDisposable
{
    private const string Region = "us-east-1";
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string root;
    private readonly string identityRoot;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly TestClock clock = new(T0);
    private readonly GcGate gate = new();
    private readonly PreconditionEvaluator pre = new();
    private readonly BucketRegistry registry;
    private readonly IdentityRegistry identityRegistry;
    private readonly BlobPool blobs;
    private readonly ObjectStore objects;
    private readonly S3XmlWriter xmlWriter = new();
    private readonly S3XmlReader xmlReader = new();
    private readonly HttpResultMapper http;
    private readonly S3BucketActionDispatcher bucketDispatcher;
    private readonly S3KeyActionDispatcher keyDispatcher;
    private readonly IServiceProvider serviceProvider;

    public S3MultiUserAuthorizationTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-s3mu-{Guid.NewGuid():N}");
        identityRoot = Path.Combine(Path.GetTempPath(), $"vessel3-s3mu-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(identityRoot);

        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        identityRegistry = new IdentityRegistry(new IdentityOptions(identityRoot), clock);
        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        objects = new ObjectStore(registry, blobs, pre, gate);
        http = new HttpResultMapper(xmlWriter);

        serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();

        IS3BucketAction[] bucketActions =
        [
            new CreateBucket(registry, http),
            new DeleteBucket(registry, http),
            new PutBucketAcl(registry, xmlReader, http),
            new GetBucketAcl(registry, xmlWriter, http),
        ];
        bucketDispatcher = new S3BucketActionDispatcher(bucketActions, registry, http);

        IS3KeyAction[] keyActions =
        [
            new PutObject(objects, registry, http, pre),
            new GetObject(objects, http, pre),
            new DeleteObject(objects, http),
        ];
        keyDispatcher = new S3KeyActionDispatcher(keyActions, registry, http);
    }

    public void Dispose()
    {
        registry.Dispose();
        identityRegistry.Dispose();
        (serviceProvider as IDisposable)?.Dispose();
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

    private async Task<HttpContext> ExecuteAsync(HttpRequest req, ISigV4Verifier verifier)
    {
        var ctx = req.HttpContext;
        ctx.RequestServices = serviceProvider;
        var resBody = new MemoryStream();
        ctx.Response.Body = resBody;

        var cors = new CorsAndAccessMiddleware(registry);
        var sigv4 = new SigV4Middleware(verifier, http);

        await cors.InvokeAsync(ctx, async c1 =>
        {
            await sigv4.InvokeAsync(c1, async c2 =>
            {
                var path = c2.Request.Path.Value ?? "/";
                if (path == "/" && HttpMethods.IsGet(c2.Request.Method))
                {
                    c2.Response.StatusCode = StatusCodes.Status200OK;
                    c2.Response.ContentType = "application/xml";
                    var caller = c2.GetCaller();
                    var buckets = caller is not null ? registry.List(caller) : registry.List();
                    await xmlWriter.WriteListBuckets(c2.Response.Body, buckets, c2.RequestAborted);
                    return;
                }

                var trimmed = path.TrimStart('/');
                var slash = trimmed.IndexOf('/');
                if (slash < 0)
                {
                    var result = await bucketDispatcher.Dispatch(c2.Request.Method, trimmed, c2);
                    await result.ExecuteAsync(c2);
                    return;
                }

                var bucket = trimmed[..slash];
                var key = trimmed[(slash + 1)..];
                var keyResult = await keyDispatcher.Dispatch(c2.Request.Method, bucket, key, c2);
                await keyResult.ExecuteAsync(c2);
            });
        });

        resBody.Position = 0;
        return ctx;
    }

    private HttpRequest SignedRequest(
        Credential cred,
        string method,
        string path,
        byte[]? payload = null,
        string? query = null,
        Dictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = serviceProvider;
        var req = ctx.Request;
        req.Method = method;
        req.Path = path;
        req.Headers.Host = "localhost";

        if (query is not null)
        {
            req.QueryString = new QueryString(query.StartsWith('?') ? query : "?" + query);
        }

        if (payload is not null)
        {
            req.Body = new MemoryStream(payload);
            req.ContentLength = payload.Length;
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers[k] = v;
            }
        }

        var amzDate = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        req.Headers["x-amz-date"] = amzDate;
        req.Headers["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD";

        var signed = new List<string> { "host", "x-amz-content-sha256", "x-amz-date" };
        if (headers is not null)
        {
            foreach (var k in headers.Keys)
            {
                var lower = k.ToLowerInvariant();
                if (!signed.Contains(lower)) signed.Add(lower);
            }
        }
        signed.Sort(StringComparer.Ordinal);

        var canonical = new StringBuilder()
            .Append(method).Append('\n')
            .Append(path).Append('\n');

        if (req.Query.Count > 0)
        {
            var pairs = new List<(string K, string V)>();
            foreach (var kv in req.Query)
            {
                var k = Uri.EscapeDataString(kv.Key);
                if (kv.Value.Count is 0)
                {
                    pairs.Add((k, string.Empty));
                }
                else
                {
                    foreach (var v in kv.Value)
                    {
                        pairs.Add((k, Uri.EscapeDataString(v ?? string.Empty)));
                    }
                }
            }
            pairs.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.K, b.K);
                return c is not 0 ? c : string.CompareOrdinal(a.V, b.V);
            });
            canonical.Append(string.Join('&', pairs.Select(p => $"{p.K}={p.V}")));
        }
        canonical.Append('\n');

        foreach (var h in signed)
        {
            canonical.Append(h).Append(':').Append(req.Headers[h].ToString().Trim()).Append('\n');
        }
        canonical.Append('\n').Append(string.Join(';', signed)).Append("\nUNSIGNED-PAYLOAD");

        var scope = $"{date}/{Region}/s3/aws4_request";
        var signature = Sign(cred.Secret, date, amzDate, scope, canonical.ToString());
        req.Headers.Authorization =
            $"AWS4-HMAC-SHA256 Credential={cred.AccessKey}/{scope}, SignedHeaders={string.Join(';', signed)}, Signature={signature}";
        return req;
    }

    private HttpRequest UnsignedRequest(string method, string path, byte[]? payload = null, Dictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = serviceProvider;
        var req = ctx.Request;
        req.Method = method;
        req.Path = path;
        req.Headers.Host = "localhost";
        if (payload is not null)
        {
            req.Body = new MemoryStream(payload);
            req.ContentLength = payload.Length;
        }
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers[k] = v;
            }
        }
        return req;
    }

    private static string Sign(string secret, string date, string amzDate, string scope, string canonical)
    {
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(date));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(Region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));
    }

    private static string ResponseBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task UnauthenticatedMode_AnyRequest_AllowsOperationsWithoutCredentials()
    {
        var verifier = new AlwaysPassVerifier();

        var createReq = UnsignedRequest("PUT", "/unauth-b");
        var createCtx = await ExecuteAsync(createReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, createCtx.Response.StatusCode);

        var ownerRes = registry.GetOwner("unauth-b");
        Assert.True(ownerRes.TryGetValue(out var owner, out _));
        Assert.Equal(CallerIdentity.System.UserId, owner);

        var putReq = UnsignedRequest("PUT", "/unauth-b/hello.txt", "hello unauth"u8.ToArray());
        var putCtx = await ExecuteAsync(putReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, putCtx.Response.StatusCode);

        var getReq = UnsignedRequest("GET", "/unauth-b/hello.txt");
        var getCtx = await ExecuteAsync(getReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);
        Assert.Equal("hello unauth", ResponseBody(getCtx));

        var listReq = UnsignedRequest("GET", "/");
        var listCtx = await ExecuteAsync(listReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, listCtx.Response.StatusCode);
        Assert.Contains("unauth-b", ResponseBody(listCtx));

        var delObjReq = UnsignedRequest("DELETE", "/unauth-b/hello.txt");
        var delObjCtx = await ExecuteAsync(delObjReq, verifier);
        Assert.Equal(StatusCodes.Status204NoContent, delObjCtx.Response.StatusCode);

        var delBucketReq = UnsignedRequest("DELETE", "/unauth-b");
        var delBucketCtx = await ExecuteAsync(delBucketReq, verifier);
        Assert.Equal(StatusCodes.Status204NoContent, delBucketCtx.Response.StatusCode);
    }

    [Fact]
    public async Task SoloAuthenticatedMode_RootKey_AcceptsRootAndRejectsUnauthenticated()
    {
        var rootCred = new Credential("AKIASOLO000000000001", "solosecret0000000000000000000000001", null, null);
        var store = new CredentialStore(rootCred, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        var createReq = SignedRequest(rootCred, "PUT", "/solo-b");
        var createCtx = await ExecuteAsync(createReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, createCtx.Response.StatusCode);

        var putReq = SignedRequest(rootCred, "PUT", "/solo-b/test.txt", "solo content"u8.ToArray());
        var putCtx = await ExecuteAsync(putReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, putCtx.Response.StatusCode);

        var getReq = SignedRequest(rootCred, "GET", "/solo-b/test.txt");
        var getCtx = await ExecuteAsync(getReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);
        Assert.Equal("solo content", ResponseBody(getCtx));

        var unauthReq = UnsignedRequest("GET", "/solo-b/test.txt");
        var unauthCtx = await ExecuteAsync(unauthReq, verifier);
        Assert.Equal(StatusCodes.Status400BadRequest, unauthCtx.Response.StatusCode);
        Assert.Contains("MissingSecurityHeader", ResponseBody(unauthCtx));

        var badSecretReq = SignedRequest(rootCred with { Secret = "badsecret" }, "GET", "/solo-b/test.txt");
        var badSecretCtx = await ExecuteAsync(badSecretReq, verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, badSecretCtx.Response.StatusCode);
        Assert.Contains("SignatureDoesNotMatch", ResponseBody(badSecretCtx));
    }

    [Fact]
    public async Task BucketCreation_MultiUser_SetsOwner()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        var createReq = SignedRequest(aliceCred, "PUT", "/alice-owned-b");
        var createCtx = await ExecuteAsync(createReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, createCtx.Response.StatusCode);

        var ownerRes = registry.GetOwner("alice-owned-b");
        Assert.True(ownerRes.TryGetValue(out var owner, out _));
        Assert.Equal(aliceUser.Id, owner);
    }

    [Fact]
    public async Task ListBuckets_MultiUser_ScopesToOwnerAndAdminSeesAll()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var bobUser = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bobUser.Id)).Value;
        var bobCred = new Credential(bobKey.Id, bobKey.SecretKey, null, null);

        var adminUser = ((Result<User>.Success)identityRegistry.CreateUser("admin", UserRole.Admin)).Value;
        var adminKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(adminUser.Id)).Value;
        var adminCred = new Credential(adminKey.Id, adminKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-list-b"), verifier);
        await ExecuteAsync(SignedRequest(bobCred, "PUT", "/bob-list-b"), verifier);

        var aliceListCtx = await ExecuteAsync(SignedRequest(aliceCred, "GET", "/"), verifier);
        Assert.Equal(StatusCodes.Status200OK, aliceListCtx.Response.StatusCode);
        var aliceXml = ResponseBody(aliceListCtx);
        Assert.Contains("alice-list-b", aliceXml);
        Assert.DoesNotContain("bob-list-b", aliceXml);

        var bobListCtx = await ExecuteAsync(SignedRequest(bobCred, "GET", "/"), verifier);
        Assert.Equal(StatusCodes.Status200OK, bobListCtx.Response.StatusCode);
        var bobXml = ResponseBody(bobListCtx);
        Assert.Contains("bob-list-b", bobXml);
        Assert.DoesNotContain("alice-list-b", bobXml);

        var adminListCtx = await ExecuteAsync(SignedRequest(adminCred, "GET", "/"), verifier);
        Assert.Equal(StatusCodes.Status200OK, adminListCtx.Response.StatusCode);
        var adminXml = ResponseBody(adminListCtx);
        Assert.Contains("alice-list-b", adminXml);
        Assert.Contains("bob-list-b", adminXml);
    }

    [Fact]
    public async Task PrivateBucket_MultiUser_EnforcesIsolation()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var bobUser = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bobUser.Id)).Value;
        var bobCred = new Credential(bobKey.Id, bobKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-iso-b"), verifier);

        var alicePutCtx = await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-iso-b/doc.txt", "confidential"u8.ToArray()), verifier);
        Assert.Equal(StatusCodes.Status200OK, alicePutCtx.Response.StatusCode);

        var aliceGetCtx = await ExecuteAsync(SignedRequest(aliceCred, "GET", "/alice-iso-b/doc.txt"), verifier);
        Assert.Equal(StatusCodes.Status200OK, aliceGetCtx.Response.StatusCode);
        Assert.Equal("confidential", ResponseBody(aliceGetCtx));

        var bobGetCtx = await ExecuteAsync(SignedRequest(bobCred, "GET", "/alice-iso-b/doc.txt"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobGetCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobGetCtx));

        var bobPutCtx = await ExecuteAsync(SignedRequest(bobCred, "PUT", "/alice-iso-b/hack.txt", "bad"u8.ToArray()), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobPutCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobPutCtx));

        var bobDelCtx = await ExecuteAsync(SignedRequest(bobCred, "DELETE", "/alice-iso-b/doc.txt"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobDelCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobDelCtx));
    }

    [Fact]
    public async Task PublicRead_MultiUser_AllowsReadAndBlocksWrite()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var bobUser = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bobUser.Id)).Value;
        var bobCred = new Credential(bobKey.Id, bobKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-pub-b"), verifier);
        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-pub-b/shared.txt", "open content"u8.ToArray()), verifier);

        var aclReq = SignedRequest(
            aliceCred,
            "PUT",
            "/alice-pub-b",
            query: "acl",
            headers: new() { ["x-amz-acl"] = "public-read" });
        var aclCtx = await ExecuteAsync(aclReq, verifier);
        Assert.Equal(StatusCodes.Status200OK, aclCtx.Response.StatusCode);

        var bobGetCtx = await ExecuteAsync(SignedRequest(bobCred, "GET", "/alice-pub-b/shared.txt"), verifier);
        Assert.Equal(StatusCodes.Status200OK, bobGetCtx.Response.StatusCode);
        Assert.Equal("open content", ResponseBody(bobGetCtx));

        var anonGetCtx = await ExecuteAsync(UnsignedRequest("GET", "/alice-pub-b/shared.txt"), verifier);
        Assert.Equal(StatusCodes.Status200OK, anonGetCtx.Response.StatusCode);
        Assert.Equal("open content", ResponseBody(anonGetCtx));

        var bobPutCtx = await ExecuteAsync(SignedRequest(bobCred, "PUT", "/alice-pub-b/shared.txt", "overwrite"u8.ToArray()), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobPutCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobPutCtx));

        var bobNewPutCtx = await ExecuteAsync(SignedRequest(bobCred, "PUT", "/alice-pub-b/bob.txt", "bob"u8.ToArray()), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobNewPutCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobNewPutCtx));
    }

    [Fact]
    public async Task ReadOnlyUser_MultiUser_DeniesMutations()
    {
        var charlieUser = ((Result<User>.Success)identityRegistry.CreateUser("charlie", UserRole.ReadOnly)).Value;
        var charlieKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(charlieUser.Id)).Value;
        var charlieCred = new Credential(charlieKey.Id, charlieKey.SecretKey, null, null);

        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        var createCtx = await ExecuteAsync(SignedRequest(charlieCred, "PUT", "/charlie-b"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, createCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(createCtx));

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-ro-b"), verifier);
        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-ro-b/data.txt", "hello"u8.ToArray()), verifier);

        var putCtx = await ExecuteAsync(SignedRequest(charlieCred, "PUT", "/alice-ro-b/data.txt", "charlie"u8.ToArray()), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, putCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(putCtx));
    }

    [Fact]
    public async Task DeleteBucket_MultiUser_RequiresOwnerOrAdmin()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var bobUser = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bobUser.Id)).Value;
        var bobCred = new Credential(bobKey.Id, bobKey.SecretKey, null, null);

        var adminUser = ((Result<User>.Success)identityRegistry.CreateUser("admin", UserRole.Admin)).Value;
        var adminKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(adminUser.Id)).Value;
        var adminCred = new Credential(adminKey.Id, adminKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-del-b"), verifier);

        var bobDelCtx = await ExecuteAsync(SignedRequest(bobCred, "DELETE", "/alice-del-b"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, bobDelCtx.Response.StatusCode);
        Assert.Contains("AccessDenied", ResponseBody(bobDelCtx));

        var aliceDelCtx = await ExecuteAsync(SignedRequest(aliceCred, "DELETE", "/alice-del-b"), verifier);
        Assert.Equal(StatusCodes.Status204NoContent, aliceDelCtx.Response.StatusCode);

        await ExecuteAsync(SignedRequest(bobCred, "PUT", "/bob-del-b"), verifier);

        var adminDelCtx = await ExecuteAsync(SignedRequest(adminCred, "DELETE", "/bob-del-b"), verifier);
        Assert.Equal(StatusCodes.Status204NoContent, adminDelCtx.Response.StatusCode);
    }

    [Fact]
    public async Task Authenticate_RevokedKeyAndSuspendedUser_DeniesAccess()
    {
        var aliceUser = ((Result<User>.Success)identityRegistry.CreateUser("alice", UserRole.Member)).Value;
        var aliceKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(aliceUser.Id)).Value;
        var aliceCred = new Credential(aliceKey.Id, aliceKey.SecretKey, null, null);

        var bobUser = ((Result<User>.Success)identityRegistry.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identityRegistry.CreateAccessKey(bobUser.Id)).Value;
        var bobCred = new Credential(bobKey.Id, bobKey.SecretKey, null, null);

        var store = new CredentialStore(null, identityRegistry, clock);
        var verifier = new SigV4Verifier(store, new ServerRegion(Region), clock);

        await ExecuteAsync(SignedRequest(aliceCred, "PUT", "/alice-auth-b"), verifier);

        identityRegistry.RevokeAccessKey(aliceKey.Id);

        var revokedCtx = await ExecuteAsync(SignedRequest(aliceCred, "GET", "/"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, revokedCtx.Response.StatusCode);
        Assert.Contains("InvalidAccessKeyId", ResponseBody(revokedCtx));

        identityRegistry.UpdateUserStatus(bobUser.Id, UserStatus.Suspended);

        var suspendedCtx = await ExecuteAsync(SignedRequest(bobCred, "GET", "/"), verifier);
        Assert.Equal(StatusCodes.Status403Forbidden, suspendedCtx.Response.StatusCode);
        Assert.Contains("InvalidAccessKeyId", ResponseBody(suspendedCtx));
    }
}
