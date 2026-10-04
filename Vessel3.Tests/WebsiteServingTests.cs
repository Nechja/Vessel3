using System.Text;
using Microsoft.AspNetCore.Http;
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
    private readonly WebsiteService websiteService;

    public WebsiteServingTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-webserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(registry);

        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        objects = new ObjectStore(registry, blobs, pre, gate);
        websiteService = new WebsiteService(registry, objects, pre);
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

    private async Task SeedObject(string bucket, string key, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var putRes = await objects.Put(new ObjectPutRequest(
            bucket, key, new MemoryStream(bytes),
            DeclaredSize: bytes.Length, ContentType: contentType,
            Ct: TestContext.Current.CancellationToken));

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

        var res = await websiteService.Serve(bucket, "/", ctx);
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

        var res = await websiteService.Serve(bucket, "/docs/", ctx);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }

    [Fact]
    public async Task Serve_DirectoryWithoutTrailingSlash_Redirects()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "docs/index.html", "<h1>Docs</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/docs", ctx);
        var redirect = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(res);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/", redirect.Url);
    }

    [Fact]
    public async Task Serve_DirectAssetFile_ServesContent()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "styles/main.css", "body { margin: 0; }", "text/css");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/styles/main.css", ctx);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/css", fileRes.ContentType);
    }

    [Fact]
    public async Task Serve_NotFoundRoute_ServesCustomErrorDocument()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "custom-404.html"));
        await SeedObject(bucket, "custom-404.html", "<h1>Page Lost</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/non-existent-page", ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }

    [Fact]
    public async Task Serve_HeadRequest_ReturnsHeadersWithoutBody()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "404.html"));
        await SeedObject(bucket, "index.html", "<h1>Hello</h1>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "HEAD";

        var res = await websiteService.Serve(bucket, "/", ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("text/html", ctx.Response.ContentType);
        Assert.True(ctx.Response.ContentLength > 0);
        Assert.False(string.IsNullOrEmpty(ctx.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task Serve_IfNoneMatchMatch_Returns304()
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

        var res = await websiteService.Serve(bucket, "/", ctx);
        var statusRes = Assert.IsAssignableFrom<IStatusCodeHttpResult>(res);
        Assert.Equal(StatusCodes.Status304NotModified, statusRes.StatusCode);
    }

    [Fact]
    public async Task Serve_WasmFile_SetsApplicationWasmMimeType()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "index.html"));
        var wasmBytes = new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 };
        var putRes = await objects.Put(new ObjectPutRequest(
            bucket, "_framework/app.wasm", new MemoryStream(wasmBytes),
            DeclaredSize: wasmBytes.Length, ContentType: "application/octet-stream",
            Ct: TestContext.Current.CancellationToken));
        Assert.True(putRes.TryGetValue(out _, out _));

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/_framework/app.wasm", ctx);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("application/wasm", fileRes.ContentType);
    }

    [Fact]
    public async Task Serve_WebmanifestFile_SetsManifestJsonMimeType()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "index.html"));
        await SeedObject(bucket, "app.webmanifest", "{}", "application/octet-stream");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/app.webmanifest", ctx);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("application/manifest+json", fileRes.ContentType);
    }

    [Fact]
    public async Task Serve_SpaFallback_Serves200WithIndexDocument()
    {
        const string bucket = "mysite";
        Assert.True(registry.Create(bucket).Match(v => v, _ => false));
        registry.SetWebsite(bucket, new WebsiteConfig("index.html", "index.html"));
        await SeedObject(bucket, "index.html", "<div id='app'>SPA</div>", "text/html");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";

        var res = await websiteService.Serve(bucket, "/users/settings/profile", ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var fileRes = Assert.IsAssignableFrom<IContentTypeHttpResult>(res);
        Assert.Equal("text/html", fileRes.ContentType);
    }
}
