using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class WebDavMultiUserAuthorizationTests : IDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<HttpClient> httpClients = [];

    public WebDavMultiUserAuthorizationTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-dav-mu-{Guid.NewGuid():N}");
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

    private async Task<(WebApplication App, HttpClient Client, IIdentityRegistry Identity)> StartServer(string subDir)
    {
        var dataDir = Path.Combine(testDir, subDir);
        Directory.CreateDirectory(dataDir);

        var config = new VesselConfig(
            DataRoot: dataDir,
            AccessKey: "rootAdminKey",
            SecretKey: "rootAdminSecret",
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

        var identity = app.Services.GetRequiredService<IIdentityRegistry>();
        return (app, client, identity);
    }

    [Fact]
    public async Task ReadOnly_User_Can_Read_And_Propfind_But_Cannot_Write_Or_Mkcol()
    {
        var (app, client, identity) = await StartServer("ro");

        var user = ((Result<User>.Success)identity.CreateUser("alice", UserRole.ReadOnly)).Value;
        var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id)).Value;

        var registry = app.Services.GetRequiredService<IBucketRegistry>();
        registry.Create("docs", user.Id);

        using var adminPut = new HttpRequestMessage(HttpMethod.Put, "/dav/docs/manual.txt");
        adminPut.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth("rootAdminKey", "rootAdminSecret"));
        adminPut.Content = new StringContent("Vessel3 Handbook", Encoding.UTF8, "text/plain");
        await client.SendAsync(adminPut);

        using var roPropfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/docs/");
        roPropfind.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        using var propRes = await client.SendAsync(roPropfind);
        Assert.Equal((HttpStatusCode)207, propRes.StatusCode);

        using var roGet = new HttpRequestMessage(HttpMethod.Get, "/dav/docs/manual.txt");
        roGet.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        using var getRes = await client.SendAsync(roGet);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        using var roPut = new HttpRequestMessage(HttpMethod.Put, "/dav/docs/new.txt");
        roPut.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        roPut.Content = new StringContent("Illegal write", Encoding.UTF8, "text/plain");
        using var putRes = await client.SendAsync(roPut);
        Assert.Equal(HttpStatusCode.Forbidden, putRes.StatusCode);

        using var roMkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/alice-bucket/");
        roMkcol.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        using var mkcolRes = await client.SendAsync(roMkcol);
        Assert.Equal(HttpStatusCode.Forbidden, mkcolRes.StatusCode);

        using var roDelete = new HttpRequestMessage(HttpMethod.Delete, "/dav/docs/manual.txt");
        roDelete.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        using var delRes = await client.SendAsync(roDelete);
        Assert.Equal(HttpStatusCode.Forbidden, delRes.StatusCode);
    }

    [Fact]
    public async Task Member_Can_Manage_Own_Bucket_And_Cannot_Write_Or_Delete_Other_Bucket()
    {
        var (_, client, identity) = await StartServer("member-isolation");

        var bob = ((Result<User>.Success)identity.CreateUser("bob", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identity.CreateAccessKey(bob.Id)).Value;

        var charlie = ((Result<User>.Success)identity.CreateUser("charlie", UserRole.Member)).Value;
        var charlieKey = ((Result<AccessKey>.Success)identity.CreateAccessKey(charlie.Id)).Value;

        using var bobMkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/bob-box/");
        bobMkcol.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(bobKey.Id, bobKey.SecretKey));
        using var bobMkcolRes = await client.SendAsync(bobMkcol);
        Assert.Equal(HttpStatusCode.Created, bobMkcolRes.StatusCode);

        using var bobPut = new HttpRequestMessage(HttpMethod.Put, "/dav/bob-box/bob-notes.txt");
        bobPut.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(bobKey.Id, bobKey.SecretKey));
        bobPut.Content = new StringContent("Bob secret notes", Encoding.UTF8, "text/plain");
        using var bobPutRes = await client.SendAsync(bobPut);
        Assert.Equal(HttpStatusCode.Created, bobPutRes.StatusCode);

        using var charliePut = new HttpRequestMessage(HttpMethod.Put, "/dav/bob-box/charlie.txt");
        charliePut.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(charlieKey.Id, charlieKey.SecretKey));
        charliePut.Content = new StringContent("Charlie trying to write to Bob's bucket", Encoding.UTF8, "text/plain");
        using var charliePutRes = await client.SendAsync(charliePut);
        Assert.Equal(HttpStatusCode.Forbidden, charliePutRes.StatusCode);

        using var charlieDel = new HttpRequestMessage(HttpMethod.Delete, "/dav/bob-box/bob-notes.txt");
        charlieDel.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(charlieKey.Id, charlieKey.SecretKey));
        using var charlieDelRes = await client.SendAsync(charlieDel);
        Assert.Equal(HttpStatusCode.Forbidden, charlieDelRes.StatusCode);
    }

    [Fact]
    public async Task Admin_User_Can_Access_And_Modify_All_Buckets()
    {
        var (_, client, identity) = await StartServer("admin-access");

        var bob = ((Result<User>.Success)identity.CreateUser("bob-admin-test", UserRole.Member)).Value;
        var bobKey = ((Result<AccessKey>.Success)identity.CreateAccessKey(bob.Id)).Value;

        var admin = ((Result<User>.Success)identity.CreateUser("boss", UserRole.Admin)).Value;
        var adminKey = ((Result<AccessKey>.Success)identity.CreateAccessKey(admin.Id)).Value;

        using var bobMkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/bobs-storage/");
        bobMkcol.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(bobKey.Id, bobKey.SecretKey));
        await client.SendAsync(bobMkcol);

        using var adminPut = new HttpRequestMessage(HttpMethod.Put, "/dav/bobs-storage/policy.txt");
        adminPut.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(adminKey.Id, adminKey.SecretKey));
        adminPut.Content = new StringContent("Admin Audit Document", Encoding.UTF8, "text/plain");
        using var adminPutRes = await client.SendAsync(adminPut);
        Assert.Equal(HttpStatusCode.Created, adminPutRes.StatusCode);

        using var adminGet = new HttpRequestMessage(HttpMethod.Get, "/dav/bobs-storage/policy.txt");
        adminGet.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(adminKey.Id, adminKey.SecretKey));
        using var adminGetRes = await client.SendAsync(adminGet);
        Assert.Equal(HttpStatusCode.OK, adminGetRes.StatusCode);
    }

    [Fact]
    public async Task Revoked_Access_Key_Returns_Unauthorized()
    {
        var (_, client, identity) = await StartServer("revocation");

        var user = ((Result<User>.Success)identity.CreateUser("dave", UserRole.Member)).Value;
        var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id)).Value;

        identity.RevokeAccessKey(key.Id);

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(key.Id, key.SecretKey));
        using var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Invalid_Credentials_Returns_Unauthorized()
    {
        var (_, client, _) = await StartServer("invalid-creds");

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth("wrongUser", "wrongPass"));
        using var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private static string BasicAuth(string user, string pass) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
}
