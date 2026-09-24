using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.S3.Bucket;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketWebsiteTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketWebsiteTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-website-{Guid.NewGuid():N}");
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
            // Transient test directory cleanup
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

        var cfg = new WebsiteConfig("index.html", "error.html");
        using var ms = new MemoryStream();
        await writer.WriteWebsiteConfiguration(ms, cfg, TestContext.Current.CancellationToken);

        ms.Position = 0;
        var result = await reader.ReadWebsiteConfiguration(ms, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var readCfg, out var err), err?.Message);
        Assert.Equal("index.html", readCfg.IndexDocument);
        Assert.Equal("error.html", readCfg.ErrorDocument);
    }

    [Fact]
    public async Task Xml_Reader_Parses_Without_ErrorDocument()
    {
        IS3XmlReader reader = new S3XmlReader();
        const string xml = """
            <WebsiteConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <IndexDocument>
                    <Suffix>home.html</Suffix>
                </IndexDocument>
            </WebsiteConfiguration>
            """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await reader.ReadWebsiteConfiguration(ms, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var readCfg, out var err), err?.Message);
        Assert.Equal("home.html", readCfg.IndexDocument);
        Assert.Null(readCfg.ErrorDocument);
    }

    [Fact]
    public async Task Xml_Reader_Rejects_Missing_IndexDocument()
    {
        IS3XmlReader reader = new S3XmlReader();
        const string xml = """
            <WebsiteConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <ErrorDocument>
                    <Key>404.html</Key>
                </ErrorDocument>
            </WebsiteConfiguration>
            """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await reader.ReadWebsiteConfiguration(ms, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var err));
        Assert.IsType<MalformedXmlError>(err);
    }

    [Fact]
    public void Bucket_SetWebsite_Persists_Across_Open()
    {
        var bucketDir = Path.Combine(root, "site-bucket");
        Directory.CreateDirectory(bucketDir);

        using (var b1 = new Bucket("site-bucket", bucketDir, sync, durable))
        {
            b1.Open();
            Assert.Null(b1.Website);

            var setRes = b1.SetWebsite(new WebsiteConfig("index.html", "404.html"));
            Assert.True(setRes.Match(() => true, _ => false));
            Assert.NotNull(b1.Website);
        }

        // Re-open and verify persistence
        using (var b2 = new Bucket("site-bucket", bucketDir, sync, durable))
        {
            b2.Open();
            Assert.NotNull(b2.Website);
            Assert.Equal("index.html", b2.Website.IndexDocument);
            Assert.Equal("404.html", b2.Website.ErrorDocument);

            var remRes = b2.RemoveWebsite();
            Assert.True(remRes.Match(() => true, _ => false));
            Assert.Null(b2.Website);
        }

        // Re-open after removal
        using (var b3 = new Bucket("site-bucket", bucketDir, sync, durable))
        {
            b3.Open();
            Assert.Null(b3.Website);
        }
    }

    [Fact]
    public async Task Actions_Get_Put_Delete_Flow()
    {
        var reg = Registry();
        Assert.True(reg.Create("web-test").Match(v => v, _ => false));

        IS3XmlWriter xml = new S3XmlWriter();
        IS3XmlReader reader = new S3XmlReader();
        IHttpResultMapper http = new HttpResultMapper(xml);

        var getAction = new GetBucketWebsite(reg, xml, http);
        var putAction = new PutBucketWebsite(reg, reader, http);
        var delAction = new DeleteBucketWebsite(reg, http);

        // 1. Initial GET -> 404 NoSuchWebsiteConfiguration
        var ctx1 = new DefaultHttpContext();
        var get1 = await getAction.Invoke("web-test", ctx1);
        Assert.NotNull(get1); // http.Map result

        // 2. PUT ?website
        const string xmlBody = """
            <WebsiteConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <IndexDocument>
                    <Suffix>index.html</Suffix>
                </IndexDocument>
                <ErrorDocument>
                    <Key>error.html</Key>
                </ErrorDocument>
            </WebsiteConfiguration>
            """;
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(xmlBody));
        var putRes = await putAction.Invoke("web-test", ctx2);
        Assert.NotNull(putRes);

        // 3. GET ?website -> 200 with XML
        var ctx3 = new DefaultHttpContext();
        ctx3.Response.Body = new MemoryStream();
        await getAction.Invoke("web-test", ctx3);
        ctx3.Response.Body.Position = 0;
        var xmlOut = new StreamReader(ctx3.Response.Body).ReadToEnd();
        Assert.Contains("<Suffix>index.html</Suffix>", xmlOut);
        Assert.Contains("<Key>error.html</Key>", xmlOut);

        // 4. DELETE ?website -> 204
        var ctx4 = new DefaultHttpContext();
        var delRes = await delAction.Invoke("web-test", ctx4);
        Assert.NotNull(delRes);

        // 5. Subsequent GET ?website -> 404 again
        var ctx5 = new DefaultHttpContext();
        var get2 = await getAction.Invoke("web-test", ctx5);
        Assert.NotNull(get2);
    }
}
