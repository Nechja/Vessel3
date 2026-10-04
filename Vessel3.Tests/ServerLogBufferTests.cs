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

public sealed class ServerLogBufferTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];

    public ServerLogBufferTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
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
    public void ServerLogBuffer_MaintainsBoundedCapacity_AndReverseChronologicalOrder()
    {
        var buffer = new ServerLogBuffer(capacity: 3);

        buffer.Log(new ServerLogEntry("1", DateTimeOffset.UtcNow, "Information", "Access", "Req 1"));
        buffer.Log(new ServerLogEntry("2", DateTimeOffset.UtcNow, "Information", "Access", "Req 2"));
        buffer.Log(new ServerLogEntry("3", DateTimeOffset.UtcNow, "Warning", "Access", "Req 3"));

        var logs = buffer.GetRecent(10);
        Assert.Equal(3, logs.Count);
        Assert.Equal("3", logs[0].Id);
        Assert.Equal("2", logs[1].Id);
        Assert.Equal("1", logs[2].Id);

        // Add 4th item, 1st should roll off
        buffer.Log(new ServerLogEntry("4", DateTimeOffset.UtcNow, "Error", "Access", "Req 4"));
        var rolled = buffer.GetRecent(10);
        Assert.Equal(3, rolled.Count);
        Assert.Equal("4", rolled[0].Id);
        Assert.Equal("3", rolled[1].Id);
        Assert.Equal("2", rolled[2].Id);
    }

    [Fact]
    public void ServerLogBuffer_FiltersByLevelAndProtocol()
    {
        var buffer = new ServerLogBuffer(capacity: 10);

        buffer.Log(new ServerLogEntry("1", DateTimeOffset.UtcNow, "Information", "Access", "S3 Put", Protocol: "s3"));
        buffer.Log(new ServerLogEntry("2", DateTimeOffset.UtcNow, "Warning", "Access", "Azure Blob", Protocol: "azure"));
        buffer.Log(new ServerLogEntry("3", DateTimeOffset.UtcNow, "Error", "Access", "S3 Get", Protocol: "s3"));
        buffer.Log(new ServerLogEntry("4", DateTimeOffset.UtcNow, "Error", "Access", "OCI Push", Protocol: "oci"));

        var errors = buffer.GetRecent(10, level: "Error");
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal("Error", e.Level));

        var s3Logs = buffer.GetRecent(10, protocol: "s3");
        Assert.Equal(2, s3Logs.Count);
        Assert.All(s3Logs, e => Assert.Equal("s3", e.Protocol));

        var s3Errors = buffer.GetRecent(10, level: "Error", protocol: "s3");
        var single = Assert.Single(s3Errors);
        Assert.Equal("3", single.Id);
    }

    [Fact]
    public void ServerLogBuffer_Clear_EmptiesBuffer()
    {
        var buffer = new ServerLogBuffer(capacity: 10);
        buffer.Log(new ServerLogEntry("1", DateTimeOffset.UtcNow, "Information", "Access", "Req 1"));
        Assert.Single(buffer.GetRecent());

        buffer.Clear();
        Assert.Empty(buffer.GetRecent());
    }

    [Fact]
    public async Task AdminLogsEndpoint_AndClientSdk_RoundtripSuccessfully()
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

        // Perform an action to generate an access log
        var createResult = await client.CreateBucketAsync("test-log-bucket");
        Assert.True(createResult.IsSuccess);

        // Fetch logs via Client SDK
        var logsResult = await client.GetServerLogsAsync(limit: 50);
        Assert.True(logsResult.IsSuccess);
        var logs = logsResult.Value;
        Assert.NotEmpty(logs);

        // Verify that the create bucket request was logged
        var createBucketLog = logs.FirstOrDefault(l => l.Action == "CreateBucket" || l.Message.Contains("test-log-bucket"));
        Assert.NotNull(createBucketLog);
        Assert.Equal("Information", createBucketLog.Level);
        Assert.Equal(200, createBucketLog.StatusCode);

        // Test clear logs
        var clearResult = await client.ClearServerLogsAsync();
        Assert.True(clearResult.IsSuccess);

        var logsAfterClear = await client.GetServerLogsAsync(limit: 50);
        Assert.True(logsAfterClear.IsSuccess);
        // It may only have the DELETE /v1/admin/logs request itself that just ran
        Assert.True(logsAfterClear.Value.Count <= 1);
    }
}
