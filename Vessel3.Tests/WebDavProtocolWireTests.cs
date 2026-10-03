using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Protocols.WebDav;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public sealed class WebDavProtocolWireTests : IDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<HttpClient> httpClients = [];

    public WebDavProtocolWireTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-dav-wire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        foreach (var client in httpClients)
        {
            client.Dispose();
        }

        foreach (var app in runningApps)
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        string? secretKey = null)
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
            Oidc: null,
            AdminUsers: null,
            ContainerReposEnabled: false,
            WebDavEnabled: true);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

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
    public async Task Options_Returns_Dav_Compliance_Headers()
    {
        var (_, client) = await StartServer("options");

        using var req = new HttpRequestMessage(HttpMethod.Options, "/dav/");
        using var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("DAV"));
        Assert.Equal("1, 2", res.Headers.GetValues("DAV").First());
        Assert.True(res.Headers.Contains("MS-Author-Via"));
        Assert.NotEmpty(res.Content.Headers.Allow);
    }

    [Fact]
    public async Task Basic_Auth_Challenges_When_Unauthenticated_And_Credentials_Required()
    {
        var (_, client) = await StartServer("auth", "admin", "secret123");

        using var unauthReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        using var unauthRes = await client.SendAsync(unauthReq);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);
        Assert.True(unauthRes.Headers.Contains("Www-Authenticate"));
        Assert.Contains("Basic", unauthRes.Headers.GetValues("Www-Authenticate").First());

        using var authReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        authReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth("admin", "secret123"));
        using var authRes = await client.SendAsync(authReq);

        Assert.Equal((HttpStatusCode)207, authRes.StatusCode);
    }

    [Fact]
    public async Task Mkcol_Creates_Bucket_And_Directory_Marker()
    {
        var (_, client) = await StartServer("mkcol");

        using var createBucket = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/test-box/");
        using var bucketRes = await client.SendAsync(createBucket);
        Assert.Equal(HttpStatusCode.Created, bucketRes.StatusCode);

        using var createDir = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/test-box/media/");
        using var dirRes = await client.SendAsync(createDir);
        Assert.Equal(HttpStatusCode.Created, dirRes.StatusCode);
    }

    [Fact]
    public async Task Put_Get_Head_And_Byte_Range()
    {
        var (_, client) = await StartServer("putget");

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/files/");
        await client.SendAsync(mkcol);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/dav/files/hello.txt");
        put.Content = new StringContent("Hello WebDAV World", Encoding.UTF8, "text/plain");
        using var putRes = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Created, putRes.StatusCode);
        Assert.True(putRes.Headers.Contains("ETag"));

        using var head = new HttpRequestMessage(HttpMethod.Head, "/dav/files/hello.txt");
        using var headRes = await client.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
        Assert.Equal(18, headRes.Content.Headers.ContentLength);

        using var getRange = new HttpRequestMessage(HttpMethod.Get, "/dav/files/hello.txt");
        getRange.Headers.Range = new RangeHeaderValue(6, 11);
        using var rangeRes = await client.SendAsync(getRange);
        Assert.Equal(HttpStatusCode.PartialContent, rangeRes.StatusCode);
        var partialText = await rangeRes.Content.ReadAsStringAsync();
        Assert.Equal("WebDAV", partialText);
    }

    [Fact]
    public async Task Propfind_Service_Root_And_Bucket()
    {
        var (_, client) = await StartServer("propfind");

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/archive/");
        await client.SendAsync(mkcol);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/dav/archive/sample.txt");
        put.Content = new StringContent("Sample Data", Encoding.UTF8, "text/plain");
        await client.SendAsync(put);

        using var propRoot = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        propRoot.Headers.Add("Depth", "1");
        using var rootRes = await client.SendAsync(propRoot);
        Assert.Equal((HttpStatusCode)207, rootRes.StatusCode);
        var rootXml = await rootRes.Content.ReadAsStringAsync();
        Assert.Contains("archive", rootXml);

        using var propBucket = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/archive/");
        propBucket.Headers.Add("Depth", "1");
        using var bRes = await client.SendAsync(propBucket);
        Assert.Equal((HttpStatusCode)207, bRes.StatusCode);
        var bXml = await bRes.Content.ReadAsStringAsync();
        Assert.Contains("sample.txt", bXml);
    }

    [Fact]
    public async Task Copy_And_Move_Operations()
    {
        var (_, client) = await StartServer("copymove");

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/workspace/");
        await client.SendAsync(mkcol);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/dav/workspace/doc.txt");
        put.Content = new StringContent("WebDAV Document", Encoding.UTF8, "text/plain");
        await client.SendAsync(put);

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/workspace/doc.txt");
        copy.Headers.Add("Destination", "/dav/workspace/doc-copy.txt");
        using var copyRes = await client.SendAsync(copy);
        Assert.Equal(HttpStatusCode.Created, copyRes.StatusCode);

        using var getCopy = new HttpRequestMessage(HttpMethod.Get, "/dav/workspace/doc-copy.txt");
        using var getCopyRes = await client.SendAsync(getCopy);
        Assert.Equal(HttpStatusCode.OK, getCopyRes.StatusCode);
        Assert.Equal("WebDAV Document", await getCopyRes.Content.ReadAsStringAsync());

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), "/dav/workspace/doc-copy.txt");
        move.Headers.Add("Destination", "/dav/workspace/doc-moved.txt");
        using var moveRes = await client.SendAsync(move);
        Assert.Equal(HttpStatusCode.Created, moveRes.StatusCode);

        using var getOld = new HttpRequestMessage(HttpMethod.Get, "/dav/workspace/doc-copy.txt");
        using var getOldRes = await client.SendAsync(getOld);
        Assert.Equal(HttpStatusCode.NotFound, getOldRes.StatusCode);

        using var getMoved = new HttpRequestMessage(HttpMethod.Get, "/dav/workspace/doc-moved.txt");
        using var getMovedRes = await client.SendAsync(getMoved);
        Assert.Equal(HttpStatusCode.OK, getMovedRes.StatusCode);
        Assert.Equal("WebDAV Document", await getMovedRes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Lock_Unlock_And_Proppatch()
    {
        var (_, client) = await StartServer("lockprop");

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/locks/");
        await client.SendAsync(mkcol);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/dav/locks/locked.txt");
        put.Content = new StringContent("Content to lock", Encoding.UTF8, "text/plain");
        await client.SendAsync(put);

        using var lockReq = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/locks/locked.txt");
        lockReq.Content = new StringContent(
            "<D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>",
            Encoding.UTF8,
            "application/xml");
        using var lockRes = await client.SendAsync(lockReq);
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);
        Assert.True(lockRes.Headers.Contains("Lock-Token"));
        var token = lockRes.Headers.GetValues("Lock-Token").First();

        using var unlockReq = new HttpRequestMessage(new HttpMethod("UNLOCK"), "/dav/locks/locked.txt");
        unlockReq.Headers.Add("Lock-Token", token);
        using var unlockRes = await client.SendAsync(unlockReq);
        Assert.Equal(HttpStatusCode.NoContent, unlockRes.StatusCode);

        using var patchReq = new HttpRequestMessage(new HttpMethod("PROPPATCH"), "/dav/locks/locked.txt");
        patchReq.Content = new StringContent("<D:propertyupdate xmlns:D=\"DAV:\"><D:set><D:prop><custom>val</custom></D:prop></D:set></D:propertyupdate>", Encoding.UTF8, "application/xml");
        using var patchRes = await client.SendAsync(patchReq);
        Assert.Equal((HttpStatusCode)207, patchRes.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Objects_And_Collections()
    {
        var (_, client) = await StartServer("del");

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/to-delete/");
        await client.SendAsync(mkcol);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/dav/to-delete/item.bin");
        put.Content = new ByteArrayContent([1, 2, 3]);
        await client.SendAsync(put);

        using var delItem = new HttpRequestMessage(HttpMethod.Delete, "/dav/to-delete/item.bin");
        using var delItemRes = await client.SendAsync(delItem);
        Assert.Equal(HttpStatusCode.NoContent, delItemRes.StatusCode);

        using var getItem = new HttpRequestMessage(HttpMethod.Get, "/dav/to-delete/item.bin");
        using var getItemRes = await client.SendAsync(getItem);
        Assert.Equal(HttpStatusCode.NotFound, getItemRes.StatusCode);

        using var delBucket = new HttpRequestMessage(HttpMethod.Delete, "/dav/to-delete/");
        using var delBucketRes = await client.SendAsync(delBucket);
        Assert.Equal(HttpStatusCode.NoContent, delBucketRes.StatusCode);
    }

    private static string BasicAuth(string user, string pass) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
}
