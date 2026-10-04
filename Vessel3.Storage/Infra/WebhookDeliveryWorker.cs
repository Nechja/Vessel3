using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel3.Primitives;

namespace Vessel3.Storage;

internal sealed partial class WebhookDeliveryWorker : BackgroundService, IWebhookEventPublisher, IWebhookDeliveryService
{
    private readonly IWebhookStore store;
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly ILogger<WebhookDeliveryWorker> logger;
    private readonly Channel<VesselEvent> channel;

    public WebhookDeliveryWorker(
        IWebhookStore store,
        HttpClient http,
        ILogger<WebhookDeliveryWorker> logger,
        TimeProvider? clock = null)
    {
        this.store = store;
        this.http = http;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
        this.channel = Channel.CreateBounded<VesselEvent>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public void Publish(VesselEvent @event)
    {
        channel.Writer.TryWrite(@event);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessEvent(evt, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeliveryError(logger, ex, evt.Type, evt.Subject);
            }
        }
    }

    private async Task ProcessEvent(VesselEvent evt, CancellationToken ct)
    {
        LogDomainEvent(logger, evt.Type, evt.Id, evt.Subject, evt.Actor ?? "anonymous");

        if (!store.ListWebhooks().TryGetValue(out var webhooks, out _))
            return;

        foreach (var webhook in webhooks)
        {
            if (!webhook.Active) continue;
            if (!MatchesFilter(webhook.EventFilters, evt.Type)) continue;
            if (!MatchesResource(webhook.ResourceFilters, evt.Subject)) continue;

            _ = DeliverToWebhook(webhook, evt, ct);
        }
    }

    private async Task DeliverToWebhook(Webhook webhook, VesselEvent evt, CancellationToken ct)
    {
        var (payloadBytes, signature) = BuildPayload(webhook, evt);
        var sw = Stopwatch.StartNew();
        int? statusCode = null;
        string? error = null;

        try
        {
            using var req = CreateWebhookRequest(webhook.Url, payloadBytes, signature, evt);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var res = await http.SendAsync(req, timeoutCts.Token);
            statusCode = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode)
            {
                error = $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            sw.Stop();
            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, error);
        }
    }

    public async Task<Result<WebhookDeliveryResult>> TestWebhook(string webhookId, CancellationToken ct = default)
    {
        var whResult = store.GetWebhook(webhookId);
        if (!whResult.TryGetValue(out var webhook, out var err))
            return err;
        if (webhook is null)
            return new NoSuchWebhookError(webhookId);

        var testEvent = new VesselEvent(
            "evt_test_" + Ulid.NewUlid().ToString(),
            "webhook.ping",
            "/vessel3",
            "test-ping",
            clock.GetUtcNow(),
            new Dictionary<string, string>
            {
                ["message"] = "Vessel3 Webhook Test Ping",
                ["webhookId"] = webhook.Id,
                ["webhookName"] = webhook.Name
            },
            Actor: "system");

        var (payloadBytes, signature) = BuildPayload(webhook, testEvent);
        var sw = Stopwatch.StartNew();
        int? statusCode = null;
        string? error = null;
        string? responseSnippet = null;

        try
        {
            using var req = CreateWebhookRequest(webhook.Url, payloadBytes, signature, testEvent);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var res = await http.SendAsync(req, timeoutCts.Token);
            sw.Stop();
            statusCode = (int)res.StatusCode;

            try
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                responseSnippet = body.Length > 500 ? body[..500] + "..." : body;
            }
            catch
            {
            }

            if (!res.IsSuccessStatusCode)
            {
                error = $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
            }

            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, error);
            return new WebhookDeliveryResult(webhook.Id, res.IsSuccessStatusCode, statusCode, sw.Elapsed, error, responseSnippet);
        }
        catch (Exception ex)
        {
            sw.Stop();
            error = ex.Message;
            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, error);
            return new WebhookDeliveryResult(webhook.Id, false, statusCode, sw.Elapsed, error, null);
        }
    }

    private static HttpRequestMessage CreateWebhookRequest(string url, byte[] payloadBytes, string? signature, VesselEvent evt)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payloadBytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cloudevents+json") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("ce-specversion", evt.SpecVersion);
        req.Headers.TryAddWithoutValidation("ce-id", evt.Id);
        req.Headers.TryAddWithoutValidation("ce-source", evt.Source);
        req.Headers.TryAddWithoutValidation("ce-type", evt.Type);
        req.Headers.TryAddWithoutValidation("ce-subject", evt.Subject);
        req.Headers.TryAddWithoutValidation("ce-time", evt.Time.ToString("O"));
        if (!string.IsNullOrEmpty(signature))
        {
            req.Headers.TryAddWithoutValidation("X-Vessel-Signature", signature);
        }
        req.Headers.TryAddWithoutValidation("User-Agent", "Vessel3-Webhook-Delivery/1.0");
        return req;
    }

    private static (byte[] Payload, string? Signature) BuildPayload(Webhook webhook, VesselEvent evt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt, WebhookJsonContext.Default.VesselEvent);

        string? sig = null;
        if (!string.IsNullOrEmpty(webhook.Secret))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhook.Secret));
            var hash = hmac.ComputeHash(bytes);
            sig = "sha256=" + Convert.ToHexStringLower(hash);
        }

        return (bytes, sig);
    }

    public static bool MatchesFilter(IReadOnlyList<string>? filters, string eventType)
    {
        if (filters is null || filters.Count == 0) return true;
        foreach (var filter in filters)
        {
            if (filter == "*" || string.Equals(filter, eventType, StringComparison.OrdinalIgnoreCase))
                return true;

            if (filter.EndsWith('*') && eventType.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool MatchesResource(IReadOnlyList<string>? filters, string resource)
    {
        if (filters is null || filters.Count == 0) return true;
        foreach (var filter in filters)
        {
            if (filter == "*" || string.Equals(filter, resource, StringComparison.OrdinalIgnoreCase))
                return true;

            if (filter.EndsWith('*') && resource.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unexpected error delivering webhooks for event {EventType}:{ResourceId}")]
    private static partial void LogDeliveryError(ILogger logger, Exception ex, string eventType, string resourceId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "EVENT [{EventType}] id={EventId} {Subject} actor={Actor}")]
    private static partial void LogDomainEvent(ILogger logger, string eventType, string eventId, string subject, string actor);
}
