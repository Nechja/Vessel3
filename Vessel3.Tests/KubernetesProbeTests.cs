using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public class KubernetesProbeTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly HttpClient httpClient = new(new SocketsHttpHandler { UseProxy = false });

    public KubernetesProbeTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-probe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
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
    public async Task Healthz_ReturnsSuccess_WithoutAuthentication()
    {
        var app = await StartServerWithAuth();
        var address = app.Urls.First();

        var response = await httpClient.GetAsync($"{address}{WellKnownRoutes.Healthz}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Status: {response.StatusCode}, Body: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Livez_ReturnsSuccess_WithoutAuthentication()
    {
        var app = await StartServerWithAuth();
        var address = app.Urls.First();

        var response = await httpClient.GetAsync($"{address}{WellKnownRoutes.Livez}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("alive", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_ReturnsSuccess_WhenStorageExists()
    {
        var app = await StartServerWithAuth();
        var address = app.Urls.First();

        var response = await httpClient.GetAsync($"{address}{WellKnownRoutes.Readyz}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }

    private async Task<WebApplication> StartServerWithAuth()
    {
        var dataRoot = Path.Combine(testDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        var config = new VesselConfig(
            DataRoot: dataRoot,
            AccessKey: "ROOTACCESSKEY123456",
            SecretKey: "ROOTSECRETKEY12345678901234567890",
            Region: "us-east-1",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(10),
            LifecycleInterval: TimeSpan.Zero,
            CompactInterval: TimeSpan.Zero,
            CompactThresholdBytes: 64 * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(10),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null);

        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.ConfigureVesselHost();
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();

        await app.StartAsync();
        runningApps.Add(app);
        return app;
    }
}
