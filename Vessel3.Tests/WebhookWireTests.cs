using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Protocols.Oci;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class WebhookWireTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<HttpClient> httpClients = [];

    public WebhookWireTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-wh-wire-{Guid.NewGuid():N}");
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

    private sealed record ReceivedWebhook(string Body, string? Signature);

    private async Task<(WebApplication App, string Url, ConcurrentQueue<ReceivedWebhook> Queue)> StartWebhookReceiver()
    {
        var queue = new ConcurrentQueue<ReceivedWebhook>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapPost("/webhook-receiver", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var sig = ctx.Request.Headers["X-Vessel-Signature"].ToString();
            queue.Enqueue(new ReceivedWebhook(body, string.IsNullOrEmpty(sig) ? null : sig));
            return Results.Ok(new { status = "received" });
        });

        await app.StartAsync();
        runningApps.Add(app);
        var url = app.Urls.First() + "/webhook-receiver";
        return (app, url, queue);
    }

    private async Task<(WebApplication App, VesselClient Client)> StartVesselServer(
        string subDir,
        string? webhooksFile = null)
    {
        var dataDir = Path.Combine(testDir, subDir);
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
            ContainerReposEnabled: true,
            WebhooksFile: webhooksFile);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var serverUrl = app.Urls.First();
        var client = new VesselClient(serverUrl);
        return (app, client);
    }

    [Fact]
    public async Task WebhookEndpoints_Crud_WorksCorrectly()
    {
        var (_, client) = await StartVesselServer("crud");

        var createReq = new CreateWebhookDto(
            "drummer-hook",
            "http://127.0.0.1:9999/wh",
            "drummer-secret",
            ["container.image.pushed"],
            ["drummer:*"],
            Active: true);

        var createRes = await client.CreateWebhookAsync(createReq);
        Assert.True(createRes.TryGetValue(out var hook, out _));
        Assert.Equal("drummer-hook", hook.Name);
        Assert.Equal("drummer-secret", hook.Secret);
        Assert.False(hook.IsStatic);

        var listRes = await client.ListWebhooksAsync();
        Assert.True(listRes.TryGetValue(out var list, out _));
        Assert.Single(list);
        Assert.Equal("drummer-hook", list[0].Name);

        var updateRes = await client.UpdateWebhookAsync(hook.Id, new UpdateWebhookDto(
            "drummer-hook-v2",
            "http://127.0.0.1:9999/wh2",
            null,
            ["container.*"],
            null,
            Active: true));
        Assert.True(updateRes.TryGetValue(out var updated, out _));
        Assert.Equal("drummer-hook-v2", updated.Name);

        var exportRes = await client.ExportWebhooksYamlAsync();
        Assert.True(exportRes.TryGetValue(out var yaml, out _));
        Assert.Contains("id: " + hook.Id, yaml, StringComparison.Ordinal);
        Assert.Contains("drummer-hook-v2", yaml, StringComparison.Ordinal);

        var delRes = await client.DeleteWebhookAsync(hook.Id);
        Assert.False(delRes.TryGetError(out _));

        var listAfterRes = await client.ListWebhooksAsync();
        Assert.True(listAfterRes.TryGetValue(out var listAfter, out _));
        Assert.Empty(listAfter);
    }

    [Fact]
    public async Task DeclarativeYaml_LoadsAtStartup_AsStaticWebhooks()
    {
        var yamlPath = Path.Combine(testDir, "test-webhooks.yaml");
        await File.WriteAllTextAsync(yamlPath, """
            webhooks:
              - id: naomi-events
                name: "Naomi Static Hook"
                url: "http://127.0.0.1:9999/naomi"
                secret: "naomi-sec"
                events:
                  - container.image.pushed
                resources:
                  - naomi:*
            """);

        var (_, client) = await StartVesselServer("yaml-load", yamlPath);

        var listRes = await client.ListWebhooksAsync();
        Assert.True(listRes.TryGetValue(out var list, out _));
        Assert.Single(list);
        var hook = list[0];
        Assert.Equal("naomi-events", hook.Id);
        Assert.Equal("Naomi Static Hook", hook.Name);
        Assert.True(hook.IsStatic);

        // Modifying static webhook should fail
        var updateRes = await client.UpdateWebhookAsync(hook.Id, new UpdateWebhookDto(
            "naomi-mod", "http://127.0.0.1:9999/naomi", null, ["*"]));
        Assert.False(updateRes.TryGetValue(out _, out _));

        // Deleting static webhook should fail
        var delRes = await client.DeleteWebhookAsync(hook.Id);
        Assert.True(delRes.TryGetError(out _));
    }

    [Fact]
    public async Task ContainerPush_DeliversSignedWebhookToReceiver()
    {
        var (_, receiverUrl, queue) = await StartWebhookReceiver();
        var (vesselApp, client) = await StartVesselServer("delivery");

        var secret = "avasarala-test-secret-789";
        var createRes = await client.CreateWebhookAsync(new CreateWebhookDto(
            "avasarala-notifier",
            receiverUrl,
            secret,
            ["container.image.pushed", "container.image.deleted"],
            ["drummer:*"],
            Active: true));
        Assert.True(createRes.TryGetValue(out var createdHook, out _));

        // Also test the ping endpoint
        var pingRes = await client.TestWebhookAsync(createdHook.Id);
        Assert.True(pingRes.TryGetValue(out var testVal, out var pingErr), $"TestWebhookAsync returned failure: {pingErr?.Code}: {pingErr?.Message}");
        Assert.True(testVal.Success, $"TestWebhook failed: StatusCode={testVal.StatusCode}, Error={testVal.ErrorMessage}, Body={testVal.ResponseBody}");
        Assert.Equal(200, testVal.StatusCode);

        // Drain the ping message from queue
        Assert.True(queue.TryDequeue(out var pingMsg));
        Assert.Contains("webhook.ping", pingMsg.Body, StringComparison.Ordinal);
        Assert.NotNull(pingMsg.Signature);
        AssertVerifyHmac(pingMsg.Body, secret, pingMsg.Signature);

        // Now push a container manifest to /v2/drummer/manifests/latest
        var vesselUrl = vesselApp.Urls.First();
        using var ociHttp = new HttpClient { BaseAddress = new Uri(vesselUrl) };

        var configJson = """{"architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""";
        var configBytes = Encoding.UTF8.GetBytes(configJson);
        var configDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(configBytes));

        var uploadPost = new HttpRequestMessage(HttpMethod.Post, $"/v2/drummer/blobs/uploads/?digest={configDigest}")
        {
            Content = new ByteArrayContent(configBytes)
        };
        var uploadRes = await ociHttp.SendAsync(uploadPost);
        var uploadErrBody = await uploadRes.Content.ReadAsStringAsync();
        Assert.True(uploadRes.StatusCode == HttpStatusCode.Created, $"Blob upload failed: {uploadRes.StatusCode} - {uploadErrBody}");

        var manifestJson = $$"""
        {
          "schemaVersion": 2,
          "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
          "config": {
            "mediaType": "application/vnd.docker.container.image.v1+json",
            "size": {{configBytes.Length}},
            "digest": "{{configDigest}}"
          },
          "layers": []
        }
        """;

        using var manifestReq = new HttpRequestMessage(HttpMethod.Put, "/v2/drummer/manifests/latest")
        {
            Content = new StringContent(manifestJson, Encoding.UTF8, "application/vnd.docker.distribution.manifest.v2+json")
        };
        var manifestRes = await ociHttp.SendAsync(manifestReq);
        Assert.Equal(HttpStatusCode.Created, manifestRes.StatusCode);

        // Wait for webhook background delivery
        ReceivedWebhook? delivered = null;
        for (var i = 0; i < 50; i++)
        {
            if (queue.TryDequeue(out var msg))
            {
                delivered = msg;
                break;
            }
            await Task.Delay(50);
        }

        Assert.NotNull(delivered);
        Assert.Contains("container.image.pushed", delivered.Body, StringComparison.Ordinal);
        Assert.Contains("drummer:latest", delivered.Body, StringComparison.Ordinal);
        Assert.NotNull(delivered.Signature);
        AssertVerifyHmac(delivered.Body, secret, delivered.Signature);

        // Now delete manifest and verify container.image.deleted event
        var delManifestRes = await ociHttp.DeleteAsync("/v2/drummer/manifests/latest");
        Assert.True(delManifestRes.IsSuccessStatusCode);

        ReceivedWebhook? delDelivered = null;
        for (var i = 0; i < 50; i++)
        {
            if (queue.TryDequeue(out var msg))
            {
                delDelivered = msg;
                break;
            }
            await Task.Delay(50);
        }

        Assert.NotNull(delDelivered);
        Assert.Contains("container.image.deleted", delDelivered.Body, StringComparison.Ordinal);
        Assert.NotNull(delDelivered.Signature);
        AssertVerifyHmac(delDelivered.Body, secret, delDelivered.Signature);
    }

    private static void AssertVerifyHmac(string body, string secret, string signatureHeader)
    {
        Assert.StartsWith("sha256=", signatureHeader, StringComparison.Ordinal);
        var expectedHex = signatureHeader["sha256=".Length..];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computedHex = Convert.ToHexStringLower(computed);

        Assert.Equal(expectedHex, computedHex);
    }
}
