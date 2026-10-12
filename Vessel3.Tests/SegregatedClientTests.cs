using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public class SegregatedClientTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<IDisposable> disposables = [];

    public SegregatedClientTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-segregated-tests-{Guid.NewGuid():N}");
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

    private async Task<(WebApplication App, IVesselClient Client)> StartServer(string subDir)
    {
        var dataDir = Path.Combine(testDir, subDir);
        Directory.CreateDirectory(dataDir);

        var config = new VesselConfig(
            DataRoot: dataDir,
            AccessKey: null,
            SecretKey: null,
            Region: "us-east-1",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(120),
            LifecycleInterval: TimeSpan.FromHours(1),
            CompactInterval: TimeSpan.FromHours(1),
            CompactThresholdBytes: 64 * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(1),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var client = new VesselClient(app.Urls.First());
        disposables.Add(client);
        return (app, client);
    }

    [Fact]
    public async Task SegregatedSubClients_ExposedAndFunctional()
    {
        var (_, client) = await StartServer("subclients");

        IBucketsClient buckets = client.Buckets;
        IObjectsClient objects = client.Objects;
        IIdentityClient identity = client.Identity;
        IAdminClient admin = client.Admin;
        IContainersClient containers = client.Containers;
        IWebhooksClient webhooks = client.Webhooks;
        IEventsClient events = client.Events;

        Assert.NotNull(buckets);
        Assert.NotNull(objects);
        Assert.NotNull(identity);
        Assert.NotNull(admin);
        Assert.NotNull(containers);
        Assert.NotNull(webhooks);
        Assert.NotNull(events);

        var who = await identity.WhoAmI();
        Assert.True(who.TryGetValue(out var whoDto, out _));
        Assert.False(string.IsNullOrEmpty(whoDto.UserId));

        var createBucket = await buckets.CreateBucket("photos");
        Assert.True(createBucket is Result.OkResult);

        var bucketList = await buckets.ListBuckets();
        Assert.True(bucketList.TryGetValue(out var bList, out _));
        Assert.Contains(bList, b => b.Name == "photos");

        var payload = "hello segregated world"u8.ToArray();
        using var putStream = new MemoryStream(payload);
        var putRes = await objects.PutObject("photos", "hello.txt", putStream, "text/plain");
        Assert.True(putRes.TryGetValue(out var putDto, out _));
        Assert.Equal(payload.Length, putDto.Size);

        var getRes = await objects.GetObject("photos", "hello.txt");
        Assert.True(getRes.TryGetValue(out var download, out _));
        using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        download.Dispose();
        Assert.Equal("hello segregated world", Encoding.UTF8.GetString(ms.ToArray()));

        var logRes = await admin.GetServerLogs(limit: 10);
        Assert.True(logRes.TryGetValue(out var logs, out _));
        Assert.NotNull(logs);

        var reposRes = await containers.ListContainerRepos();
        Assert.True(reposRes.TryGetValue(out var repos, out _));
        Assert.NotNull(repos);

        var webhooksRes = await webhooks.ListWebhooks();
        Assert.True(webhooksRes.TryGetValue(out var hooks, out _));
        Assert.NotNull(hooks);
    }

    [Fact]
    public async Task CompositeFacade_ForwardsToSubClientsDirectly()
    {
        var (_, client) = await StartServer("facade");

        var createRes = await client.CreateBucket("documents");
        Assert.True(createRes is Result.OkResult);

        var data = "facade test"u8.ToArray();
        using var stream = new MemoryStream(data);
        var putRes = await client.PutObject("documents", "doc.txt", stream, "text/plain");
        Assert.True(putRes.TryGetValue(out var putDto, out _));
        Assert.Equal(data.Length, putDto.Size);

        var statRes = await client.StatObject("documents", "doc.txt");
        Assert.True(statRes.TryGetValue(out var stat, out _));
        Assert.Equal(data.Length, stat.Size);

        var delRes = await client.DeleteObject("documents", "doc.txt");
        Assert.True(delRes is Result.OkResult);
    }
}
