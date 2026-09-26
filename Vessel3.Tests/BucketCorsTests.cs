using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.S3.Bucket;
using Xunit;

namespace Vessel3.Tests;

public class BucketCorsTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketCorsTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-cors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
        }
    }

    private BucketRegistry Registry()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(reg);
        return reg;
    }

    [Fact]
    public async Task Xml_Reader_And_Writer_RoundTrip()
    {
        IS3XmlWriter writer = new S3XmlWriter();
        IS3XmlReader reader = new S3XmlReader();

        var cfg = new CorsConfig([
            new CorsRule(
                AllowedOrigins: ["http://localhost:3000", "https://*.example.com"],
                AllowedMethods: ["GET", "PUT", "DELETE"],
                AllowedHeaders: ["Authorization", "x-amz-*"],
                ExposeHeaders: ["ETag", "x-amz-request-id"],
                MaxAgeSeconds: 3600,
                Id: "Rule1"),
            new CorsRule(
                AllowedOrigins: ["*"],
                AllowedMethods: ["GET"],
                Id: "PublicGet")
        ]);

        using var ms = new MemoryStream();
        await writer.WriteCorsConfiguration(ms, cfg, TestContext.Current.CancellationToken);

        ms.Position = 0;
        var result = await reader.ReadCorsConfiguration(ms, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var readCfg, out var err), err?.Message);
        Assert.Equal(2, readCfg.Rules.Count);

        var r1 = readCfg.Rules[0];
        Assert.Equal("Rule1", r1.Id);
        Assert.Equal(["http://localhost:3000", "https://*.example.com"], r1.AllowedOrigins);
        Assert.Equal(["GET", "PUT", "DELETE"], r1.AllowedMethods);
        Assert.Equal(["Authorization", "x-amz-*"], r1.AllowedHeaders);
        Assert.Equal(["ETag", "x-amz-request-id"], r1.ExposeHeaders);
        Assert.Equal(3600, r1.MaxAgeSeconds);

        var r2 = readCfg.Rules[1];
        Assert.Equal("PublicGet", r2.Id);
        Assert.Equal(["*"], r2.AllowedOrigins);
        Assert.Equal(["GET"], r2.AllowedMethods);
    }

    [Fact]
    public async Task Xml_Reader_Rejects_Empty_Rules()
    {
        IS3XmlReader reader = new S3XmlReader();
        const string xml = """
            <CORSConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
            </CORSConfiguration>
            """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await reader.ReadCorsConfiguration(ms, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var err));
        Assert.IsType<MalformedXmlError>(err);
    }

    [Fact]
    public async Task Xml_Reader_Rejects_Rule_Missing_Origin_Or_Method()
    {
        IS3XmlReader reader = new S3XmlReader();
        const string missingMethod = """
            <CORSConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <CORSRule>
                    <AllowedOrigin>*</AllowedOrigin>
                </CORSRule>
            </CORSConfiguration>
            """;

        using var ms1 = new MemoryStream(Encoding.UTF8.GetBytes(missingMethod));
        var res1 = await reader.ReadCorsConfiguration(ms1, TestContext.Current.CancellationToken);
        Assert.False(res1.TryGetValue(out _, out var err1));
        Assert.IsType<MalformedXmlError>(err1);

        const string missingOrigin = """
            <CORSConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <CORSRule>
                    <AllowedMethod>GET</AllowedMethod>
                </CORSRule>
            </CORSConfiguration>
            """;

        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(missingOrigin));
        var res2 = await reader.ReadCorsConfiguration(ms2, TestContext.Current.CancellationToken);
        Assert.False(res2.TryGetValue(out _, out var err2));
        Assert.IsType<MalformedXmlError>(err2);
    }

    [Fact]
    public void Rule_Matching_Origin_Method_And_Headers()
    {
        var rule = new CorsRule(
            AllowedOrigins: ["http://localhost:3000", "https://*.app.io"],
            AllowedMethods: ["GET", "PUT"],
            AllowedHeaders: ["Authorization", "x-amz-*"]);

        Assert.True(rule.MatchesOrigin("http://localhost:3000"));
        Assert.True(rule.MatchesOrigin("https://sub.app.io"));
        Assert.False(rule.MatchesOrigin("https://evil.com"));

        Assert.True(rule.MatchesMethod("GET"));
        Assert.True(rule.MatchesMethod("get"));
        Assert.True(rule.MatchesMethod("PUT"));
        Assert.False(rule.MatchesMethod("DELETE"));

        Assert.True(rule.MatchesHeaders(["Authorization", "x-amz-date", "x-amz-content-sha256"]));
        Assert.False(rule.MatchesHeaders(["X-Custom-Forbidden-Header"]));

        Assert.True(CorsRule.WildcardMatch("*", "https://anything.com"));
        Assert.True(CorsRule.WildcardMatch("https://*.example.com", "https://sub.example.com"));
        Assert.False(CorsRule.WildcardMatch("https://*.example.com", "https://sub.example.com.evil.com"));
        Assert.False(CorsRule.WildcardMatch("https://*.example.com", "https://sub.example.com/path"));
        Assert.True(CorsRule.WildcardMatch("https://*.*.example.com", "https://a.b.example.com"));
        Assert.False(CorsRule.WildcardMatch("https://*.*.example.com", "https://a.b.example.com/evil"));
        Assert.True(CorsRule.WildcardMatch("x-amz-*", "x-amz-date"));
        Assert.False(CorsRule.WildcardMatch("x-amz-*", "authorization"));
    }

    [Fact]
    public async Task Get_Put_Delete_BucketCors_Lifecycle()
    {
        var reg = Registry();
        reg.Create("test-cors");

        IS3XmlWriter xml = new S3XmlWriter();
        IS3XmlReader reader = new S3XmlReader();
        IHttpResultMapper http = new HttpResultMapper(xml);

        var get = new GetBucketCors(reg, xml, http);
        var put = new PutBucketCors(reg, reader, http);
        var del = new DeleteBucketCors(reg, http);

        var ctx1 = new DefaultHttpContext();
        var res1 = await get.Invoke("test-cors", ctx1);
        Assert.IsType<S3ErrorResult>(res1);

        const string corsXml = """
            <CORSConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <CORSRule>
                    <AllowedOrigin>*</AllowedOrigin>
                    <AllowedMethod>GET</AllowedMethod>
                    <AllowedMethod>HEAD</AllowedMethod>
                </CORSRule>
            </CORSConfiguration>
            """;
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corsXml));
        var res2 = await put.Invoke("test-cors", ctx2);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(res2);

        var ctx3 = new DefaultHttpContext();
        var ms3 = new MemoryStream();
        ctx3.Response.Body = ms3;
        var res3 = await get.Invoke("test-cors", ctx3);
        Assert.NotNull(res3);
        ms3.Position = 0;
        var parsed = await reader.ReadCorsConfiguration(ms3, TestContext.Current.CancellationToken);
        Assert.True(parsed.TryGetValue(out var cfg, out _));
        Assert.Single(cfg.Rules);
        Assert.Equal(["*"], cfg.Rules[0].AllowedOrigins);

        var ctx4 = new DefaultHttpContext();
        var res4 = await del.Invoke("test-cors", ctx4);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(res4);

        var ctx5 = new DefaultHttpContext();
        var res5 = await get.Invoke("test-cors", ctx5);
        Assert.IsType<S3ErrorResult>(res5);
    }

    [Fact]
    public async Task CorsAndAccessMiddleware_Preflight_MatchingOrigin_ReturnsOkWithHeaders()
    {
        var reg = Registry();
        reg.Create("cors-bucket");
        reg.SetCors("cors-bucket", new CorsConfig([
            new CorsRule(
                AllowedOrigins: ["http://localhost:3000"],
                AllowedMethods: ["PUT"],
                AllowedHeaders: ["content-type", "x-amz-*"],
                ExposeHeaders: ["ETag"],
                MaxAgeSeconds: 1800)
        ]));

        var middleware = new CorsAndAccessMiddleware(reg);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "OPTIONS";
        ctx.Request.Path = "/cors-bucket/my-key";
        ctx.Request.Headers.Origin = "http://localhost:3000";
        ctx.Request.Headers["Access-Control-Request-Method"] = "PUT";
        ctx.Request.Headers["Access-Control-Request-Headers"] = "content-type, x-amz-date";

        var nextCalled = false;
        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("http://localhost:3000", ctx.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Equal("PUT", ctx.Response.Headers.AccessControlAllowMethods.ToString());
        Assert.Equal("content-type, x-amz-date", ctx.Response.Headers.AccessControlAllowHeaders.ToString());
        Assert.Equal("1800", ctx.Response.Headers.AccessControlMaxAge.ToString());
    }

    [Fact]
    public async Task CorsAndAccessMiddleware_Preflight_ForbiddenOrigin_ReturnsForbidden()
    {
        var reg = Registry();
        reg.Create("cors-bucket");
        reg.SetCors("cors-bucket", new CorsConfig([
            new CorsRule(
                AllowedOrigins: ["http://localhost:3000"],
                AllowedMethods: ["PUT"])
        ]));

        var middleware = new CorsAndAccessMiddleware(reg);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "OPTIONS";
        ctx.Request.Path = "/cors-bucket/my-key";
        ctx.Request.Headers.Origin = "http://evil.com";
        ctx.Request.Headers["Access-Control-Request-Method"] = "PUT";

        await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);
        Assert.Equal(403, ctx.Response.StatusCode);
    }
}
