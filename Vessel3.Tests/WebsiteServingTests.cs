using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class WebsiteServingTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];
    private readonly BucketRegistry registry;
    private readonly BlobPool blobs;
    private readonly ObjectStore objects;
    private readonly GcGate gate = new();
    private readonly PreconditionEvaluator pre = new();

    public WebsiteServingTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-webserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(registry);

        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        objects = new ObjectStore(registry, blobs, pre, gate);
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

    private async Task SeedObject(string bucket, string key, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var putRes = await objects.Put(
            bucket, key, new MemoryStream(bytes),
            declaredSize: bytes.Length, contentType: contentType,
            declaredSha256: null, declaredMd5Base64: null,
            metadata: new Dictionary<string, string>(), tags: new Dictionary<string, string>(),
            declaredChecksums: ChecksumSet.Empty, ct: TestContext.Current.CancellationToken);

        Assert.True(putRes.TryGetValue(out _, out var err), err?.Message);
    }

    [Fact]
    public async Task Serves_Root_IndexDocument()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "index.html", "<h1>Hello World</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await WebsiteHandler.Serve(bucket, "/", ctx, objects, registry, pre);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }

    [Fact]
    public async Task Serves_Subfolder_IndexDocument()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "docs/index.html", "<h1>Documentation</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await WebsiteHandler.Serve(bucket, "/docs/", ctx, objects, registry, pre);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }

    [Fact]
    public async Task Redirects_Directory_Without_Trailing_Slash()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "docs/index.html", "<h1>Docs</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await WebsiteHandler.Serve(bucket, "/docs", ctx, objects, registry, pre);
        var redirect = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(res);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/", redirect.Url);
    }

    [Fact]
    public async Task Serves_Direct_Asset_File()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "styles/main.css", "body { margin: 0; }", "text/css");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await WebsiteHandler.Serve(bucket, "/styles/main.css", ctx, objects, registry, pre);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/css", fileRes.ContentType);
    }

    [Fact]
    public async Task Serves_Custom_ErrorDocument_On_404()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "custom-404.html"));
        await SeedObject(bucket, "custom-404.html", "<h1>Page Lost</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await WebsiteHandler.Serve(bucket, "/non-existent-page", ctx, objects, registry, pre);
        Assert.Equal(404, ctx.Response.StatusCode);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }

    [Fact]
    public async Task Head_Request_Returns_Headers()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "index.html", "<h1>Hello</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "HEAD";

        var res = await WebsiteHandler.Serve(bucket, "/", ctx, objects, registry, pre);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("text/html", ctx.Response.ContentType);
        Assert.True(ctx.Response.ContentLength > 0);
        Assert.False(string.IsNullOrEmpty(ctx.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task Precondition_IfNoneMatch_Returns_304()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "index.html", "<h1>Hello</h1>", "text/html");

        var stat = objects.Stat(bucket, "index.html", versionId: null);
        Assert.True(stat.TryGetValue(out var s, out _));

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Headers.IfNoneMatch = $"\"{s.Etag}\"";

        var res = await WebsiteHandler.Serve(bucket, "/", ctx, objects, registry, pre);
        var statusRes = Assert.IsAssignableFrom<IStatusCodeHttpResult>(res);
        Assert.Equal(StatusCodes.Status304NotModified, statusRes.StatusCode);
    }
}
