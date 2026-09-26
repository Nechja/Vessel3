using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Server;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class VirtualHostRoutingTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];
    private readonly BucketRegistry registry;

    public VirtualHostRoutingTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-vhroute-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(registry);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient test directory cleanup
        }
    }

    [Fact]
    public void Admin_Host_Root_Redirects_To_Ui()
    {
        string[] baseDomains = ["s3.local", "localhost"];
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("admin.localhost", 9000);
        ctx.Request.Path = "/";

        var isHandled = false;
        if (VirtualHostParser.IsAdminHost(ctx.Request.Host.Value, baseDomains))
        {
            if (ctx.Request.Path == "/" || !ctx.Request.Path.StartsWithSegments("/_ui"))
            {
                var target = ctx.Request.Path == "/" ? "/_ui/" : $"/_ui{ctx.Request.Path}{ctx.Request.QueryString}";
                ctx.Response.Redirect(target, permanent: false);
                isHandled = true;
            }
        }

        Assert.True(isHandled);
        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        Assert.Equal("/_ui/", ctx.Response.Headers.Location);
    }

    [Fact]
    public void Admin_Host_Subpath_Redirects_To_Ui_Prefix()
    {
        string[] baseDomains = ["s3.local", "localhost"];
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("admin.s3.local");
        ctx.Request.Path = "/browse";
        ctx.Request.QueryString = new QueryString("?prefix=photos/");

        var isHandled = false;
        if (VirtualHostParser.IsAdminHost(ctx.Request.Host.Value, baseDomains))
        {
            if (ctx.Request.Path == "/" || !ctx.Request.Path.StartsWithSegments("/_ui"))
            {
                var target = ctx.Request.Path == "/" ? "/_ui/" : $"/_ui{ctx.Request.Path}{ctx.Request.QueryString}";
                ctx.Response.Redirect(target, permanent: false);
                isHandled = true;
            }
        }

        Assert.True(isHandled);
        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        Assert.Equal("/_ui/browse?prefix=photos/", ctx.Response.Headers.Location);
    }

    [Fact]
    public void SigV4_Verifier_Passes_Virtual_Host_Canonical_Uri()
    {
        const string ak = "AKIAIOSFODNN7EXAMPLE";
        const string sk = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        const string region = "us-east-1";
        const string date = "20260923";
        const string amzDate = "20260923T120000Z";

        var credStore = new CredentialStore(new Credential(ak, sk, null, null), TimeProvider.System);
        var clock = new TestClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var verifier = new SigV4Verifier(credStore, new ServerRegion(region), clock);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Host = new HostString("mybucket.s3.local", 9000);
        ctx.Request.Path = "/test-key.txt";
        ctx.Request.Headers["Host"] = "mybucket.s3.local:9000";
        ctx.Request.Headers["x-amz-date"] = amzDate;
        ctx.Request.Headers["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // Empty SHA256

        // Compute expected SigV4 for virtual-host request
        var canonicalUri = "/test-key.txt";
        var canonicalHeaders = "host:mybucket.s3.local:9000\nx-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\nx-amz-date:20260923T120000Z\n";
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"GET\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var canonicalHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{canonicalHash}";

        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{sk}"), Encoding.UTF8.GetBytes(date));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));

        ctx.Request.Headers.Authorization = $"AWS4-HMAC-SHA256 Credential={ak}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var verifyResult = verifier.Verify(ctx.Request);
        Assert.True(verifyResult.TryGetValue(out var sigCtx, out var err), err?.Message);
        Assert.Equal(signature, sigCtx.Signature);
    }
}
