using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class EventStreamingTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<HttpClient> httpClients = [];

    public EventStreamingTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-stream-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in httpClients)
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

    [Fact]
    public void EventStreamHub_MatchesTopics_Correctly()
    {
        Assert.True(EventStreamHub.MatchesTopic("object.created", null));
        Assert.True(EventStreamHub.MatchesTopic("object.created", "*"));
        Assert.True(EventStreamHub.MatchesTopic("object.created", "object.created"));
        Assert.True(EventStreamHub.MatchesTopic("object.created", "object.*"));
        Assert.True(EventStreamHub.MatchesTopic("object.deleted", "object.*"));
        Assert.True(EventStreamHub.MatchesTopic("container.image.pushed", "object.*, container.*"));

        Assert.False(EventStreamHub.MatchesTopic("user.created", "object.*"));
        Assert.False(EventStreamHub.MatchesTopic("user.created", "object.created, bucket.*"));
    }

    [Fact]
    public void EventStreamHub_MatchesResources_Correctly()
    {
        Assert.True(EventStreamHub.MatchesResource("my-bucket/photo.jpg", null));
        Assert.True(EventStreamHub.MatchesResource("my-bucket/photo.jpg", "*"));
        Assert.True(EventStreamHub.MatchesResource("my-bucket/photo.jpg", "my-bucket/*"));
        Assert.True(EventStreamHub.MatchesResource("my-bucket/sub/folder/file.txt", "my-bucket/sub/*"));

        Assert.False(EventStreamHub.MatchesResource("other-bucket/photo.jpg", "my-bucket/*"));
        Assert.False(EventStreamHub.MatchesResource("my-bucket/photo.jpg", "other-bucket/*"));
    }

    [Fact]
    public async Task EventStreamHub_BroadcastsAndEvicts_Subscribers()
    {
        var hub = new EventStreamHub();
        using var sub1 = hub.Subscribe("object.*", "bucket-a/*");
        using var sub2 = hub.Subscribe("user.*");

        var evt1 = VesselEvents.ObjectCreated("bucket-a", "file1.txt", 100, "etag1", "v1", "sha1", "text/plain");
        var evt2 = VesselEvents.UserCreated("usr_123", "Member");

        hub.Publish(evt1);
        hub.Publish(evt2);

        Assert.True(sub1.Reader.TryRead(out var receivedBySub1));
        Assert.Equal("object.created", receivedBySub1.Type);
        Assert.Equal("bucket-a/file1.txt", receivedBySub1.Subject);
        Assert.False(sub1.Reader.TryRead(out _));

        Assert.True(sub2.Reader.TryRead(out var receivedBySub2));
        Assert.Equal("user.created", receivedBySub2.Type);
        Assert.Equal("usr_123", receivedBySub2.Subject);
        Assert.False(sub2.Reader.TryRead(out _));

        // Test disposal
        sub1.Dispose();
        await sub1.Reader.Completion;
    }

    [Fact]
    public async Task EventStreamEndpoints_StreamsSSEFrames_OverHttp()
    {
        var dataDir = Path.Combine(testDir, "server");
        Directory.CreateDirectory(dataDir);

        var config = new VesselConfig(
            DataRoot: dataDir,
            AccessKey: null,
            SecretKey: null,
            Region: "us-west-2",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(120),
            LifecycleInterval: TimeSpan.FromHours(1),
            CompactInterval: TimeSpan.FromHours(1),
            CompactThresholdBytes: 64 * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(1),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null,
            OciEnabled: true);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var serverUrl = app.Urls.First();
        using var client = new VesselClient(serverUrl);
        var hub = app.Services.GetRequiredService<IEventStreamHub>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.StreamEventsAsync("object.*", ct: cts.Token).GetAsyncEnumerator(cts.Token);

        // Advance to establish connection
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        // Wait until SSE connection is established and subscribed
        while (hub.SubscriberCount == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        // Publish event
        var domainEvent = VesselEvents.ObjectCreated("photos", "vacation.png", 2048, "etag-123", "v1", "sha256-abc", "image/png");
        hub.Publish(domainEvent);

        var moved = await moveNextTask;
        Assert.True(moved);
        var received = enumerator.Current;

        Assert.NotNull(received);
        Assert.Equal("object.created", received.Type);
        Assert.Equal("photos/vacation.png", received.Subject);
        Assert.NotNull(received.Data);
        Assert.Equal("photos", received.Data["bucket"]);
        Assert.Equal("vacation.png", received.Data["key"]);

        await enumerator.DisposeAsync();
    }
}
