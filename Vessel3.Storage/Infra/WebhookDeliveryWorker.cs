using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Vessel3.Storage;

internal sealed partial class WebhookDeliveryWorker : BackgroundService, IWebhookEventPublisher, IWebhookDeliveryService
{
    private readonly IWebhookStore store;
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly ILogger<WebhookDeliveryWorker> logger;
    private readonly Channel<VesselEvent> channel;
    private readonly IEventStreamHub? streamHub;

    public WebhookDeliveryWorker(
        IWebhookStore store,
        HttpClient http,
        ILogger<WebhookDeliveryWorker> logger,
        TimeProvider? clock = null,
        IEventStreamHub? streamHub = null)
    {
        this.store = store;
        this.http = http;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
        this.streamHub = streamHub;
        this.channel = Channel.CreateBounded<VesselEvent>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public void Publish(VesselEvent @event)
    {
        channel.Writer.TryWrite(@event);
        streamHub?.Publish(@event);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var domainEvent in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessEvent(domainEvent, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDeliveryError(logger, exception, domainEvent.Type, domainEvent.Subject);
            }
        }
    }

    private async Task ProcessEvent(VesselEvent domainEvent, CancellationToken cancellationToken)
    {
        LogDomainEvent(logger, domainEvent.Type, domainEvent.Id, domainEvent.Subject, domainEvent.Actor ?? "anonymous");

        if (!store.ListWebhooks().TryGetValue(out var webhooks, out _))
            return;

        foreach (var webhook in webhooks)
        {
            if (!webhook.Active) continue;
            if (!MatchesFilter(webhook.EventFilters, domainEvent.Type)) continue;
            if (!MatchesResource(webhook.ResourceFilters, domainEvent.Subject)) continue;

            _ = DeliverToWebhook(webhook, domainEvent, cancellationToken);
        }
    }

    private async Task DeliverToWebhook(Webhook webhook, VesselEvent domainEvent, CancellationToken cancellationToken)
    {
        var (payloadBytes, signature) = BuildPayload(webhook, domainEvent);
        int? statusCode = null;
        string? error = null;

        try
        {
            var result = await SendWebhookHttpRequestAsync(webhook.Url, payloadBytes, signature, domainEvent, cancellationToken);
            statusCode = result.StatusCode;
            error = result.Error;
            result.Response?.Dispose();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, error);
        }
    }

    public async Task<Result<WebhookDeliveryResult>> TestWebhook(string webhookId, CancellationToken cancellationToken = default)
    {
        var webhookResult = store.GetWebhook(webhookId);
        if (!webhookResult.TryGetValue(out var webhook, out var error))
            return error;
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
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        string? deliveryError = null;
        string? responseSnippet = null;

        try
        {
            var result = await SendWebhookHttpRequestAsync(webhook.Url, payloadBytes, signature, testEvent, cancellationToken);
            stopwatch.Stop();
            statusCode = result.StatusCode;
            deliveryError = result.Error;

            if (result.Response is not null)
            {
                using var response = result.Response;
                try
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    responseSnippet = body.Length > 500 ? body[..500] + "..." : body;
                }
                catch
                {
                }
            }

            var isSuccess = result.Response?.IsSuccessStatusCode ?? false;
            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, deliveryError);
            return new WebhookDeliveryResult(webhook.Id, isSuccess, statusCode, stopwatch.Elapsed, deliveryError, responseSnippet);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            deliveryError = exception.Message;
            store.RecordDeliveryResult(webhook.Id, clock.GetUtcNow(), statusCode, deliveryError);
            return new WebhookDeliveryResult(webhook.Id, false, statusCode, stopwatch.Elapsed, deliveryError, null);
        }
    }

    private async Task<(int? StatusCode, string? Error, HttpResponseMessage? Response)> SendWebhookHttpRequestAsync(
        string url,
        byte[] payloadBytes,
        string? signature,
        VesselEvent domainEvent,
        CancellationToken cancellationToken)
    {
        using var request = CreateWebhookRequest(url, payloadBytes, signature, domainEvent);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        var response = await http.SendAsync(request, timeoutCts.Token);
        var statusCode = (int)response.StatusCode;
        var error = !response.IsSuccessStatusCode
            ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            : null;

        return (statusCode, error, response);
    }

    private static HttpRequestMessage CreateWebhookRequest(string url, byte[] payloadBytes, string? signature, VesselEvent domainEvent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payloadBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cloudevents+json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("ce-specversion", domainEvent.SpecVersion);
        request.Headers.TryAddWithoutValidation("ce-id", domainEvent.Id);
        request.Headers.TryAddWithoutValidation("ce-source", domainEvent.Source);
        request.Headers.TryAddWithoutValidation("ce-type", domainEvent.Type);
        request.Headers.TryAddWithoutValidation("ce-subject", domainEvent.Subject);
        request.Headers.TryAddWithoutValidation("ce-time", domainEvent.Time.ToString("O"));
        if (!string.IsNullOrEmpty(domainEvent.Actor))
        {
            request.Headers.TryAddWithoutValidation("ce-actor", domainEvent.Actor);
        }
        if (!string.IsNullOrEmpty(domainEvent.Host))
        {
            request.Headers.TryAddWithoutValidation("ce-host", domainEvent.Host);
        }
        if (!string.IsNullOrEmpty(signature))
        {
            request.Headers.TryAddWithoutValidation("X-Vessel-Signature", signature);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "Vessel3-Webhook-Delivery/1.0");
        return request;
    }

    private static (byte[] Payload, string? Signature) BuildPayload(Webhook webhook, VesselEvent domainEvent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(domainEvent, WebhookJsonContext.Default.VesselEvent);

        string? signature = null;
        if (!string.IsNullOrEmpty(webhook.Secret))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhook.Secret));
            var hash = hmac.ComputeHash(bytes);
            signature = "sha256=" + Convert.ToHexStringLower(hash);
        }

        return (bytes, signature);
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
