using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Vessel3.Server.Configuration;

namespace Vessel3.Server.Telemetry.Otlp;

public sealed partial class OtlpTraceExporter : BackgroundService
{
    private readonly OtelConfig config;
    private readonly HttpClient httpClient;
    private readonly ILogger<OtlpTraceExporter> logger;
    private readonly Channel<OtlpSpan> channel;
    private readonly ActivityListener listener;

    public OtlpTraceExporter(
        OtelConfig config,
        HttpClient httpClient,
        ILogger<OtlpTraceExporter> logger)
    {
        this.config = config;
        this.httpClient = httpClient;
        this.logger = logger;
        channel = Channel.CreateBounded<OtlpSpan>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Vessel3",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(listener);
    }

    private void OnActivityStopped(Activity activity)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            return;
        }

        var startUnixNano = ToUnixNano(activity.StartTimeUtc);
        var endUnixNano = ToUnixNano(activity.StartTimeUtc + activity.Duration);

        List<OtlpKeyValue> attributes = [];
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Value is null)
            {
                continue;
            }

            var val = tag.Value switch
            {
                string s => new OtlpAnyValue(StringValue: s),
                int i => new OtlpAnyValue(IntValue: i.ToString()),
                long l => new OtlpAnyValue(IntValue: l.ToString()),
                bool b => new OtlpAnyValue(BoolValue: b),
                double d => new OtlpAnyValue(DoubleValue: d),
                _ => new OtlpAnyValue(StringValue: tag.Value.ToString())
            };
            attributes.Add(new OtlpKeyValue(tag.Key, val));
        }

        var statusCode = activity.Status switch
        {
            ActivityStatusCode.Ok => 1,
            ActivityStatusCode.Error => 2,
            _ => 0
        };

        var span = new OtlpSpan(
            TraceId: activity.TraceId.ToHexString(),
            SpanId: activity.SpanId.ToHexString(),
            ParentSpanId: activity.ParentSpanId != default ? activity.ParentSpanId.ToHexString() : null,
            Name: activity.DisplayName,
            Kind: (int)activity.Kind,
            StartTimeUnixNano: startUnixNano.ToString(),
            EndTimeUnixNano: endUnixNano.ToString(),
            Attributes: attributes,
            Status: new OtlpStatus(statusCode, activity.StatusDescription));

        channel.Writer.TryWrite(span);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogExporterStarted(logger, config.Endpoint ?? "(none)");

        var batch = new List<OtlpSpan>(100);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await channel.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (batch.Count < 100 && channel.Reader.TryRead(out var span))
                    {
                        batch.Add(span);
                    }

                    if (batch.Count > 0)
                    {
                        await ExportBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogExportError(logger, ex.Message);
            }
        }

        while (channel.Reader.TryRead(out var span))
        {
            batch.Add(span);
        }

        if (batch.Count > 0)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ExportBatchAsync(batch, timeoutCts.Token);
        }

        LogExporterStopped(logger);
    }

    private async Task ExportBatchAsync(IReadOnlyList<OtlpSpan> spans, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || spans.Count == 0)
        {
            return;
        }

        try
        {
            var payload = new OtlpTracePayload(
            [
                new OtlpResourceSpans(
                    Resource: new OtlpResource(
                    [
                        new OtlpKeyValue("service.name", new OtlpAnyValue(StringValue: config.ServiceName))
                    ]),
                    ScopeSpans:
                    [
                        new OtlpScopeSpans(
                            Scope: new OtlpScope("Vessel3", "1.0.0"),
                            Spans: spans)
                    ])
            ]);

            var json = JsonSerializer.Serialize(payload, OtlpJsonContext.Default.OtlpTracePayload);
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogHttpExportFailed(logger, (int)response.StatusCode, config.Endpoint);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogExportError(logger, ex.Message);
        }
    }

    private static long ToUnixNano(DateTimeOffset time) =>
        time.ToUnixTimeMilliseconds() * 1_000_000L;

    public override void Dispose()
    {
        listener.Dispose();
        base.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "OTel Trace Exporter started, target: {Endpoint}")]
    private static partial void LogExporterStarted(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OTel Trace Exporter stopped")]
    private static partial void LogExporterStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to export traces to OTLP collector: {Error}")]
    private static partial void LogExportError(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "OTLP collector returned HTTP {StatusCode} from {Endpoint}")]
    private static partial void LogHttpExportFailed(ILogger logger, int statusCode, string endpoint);
}
