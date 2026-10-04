using Vessel3.Server.Configuration;
using Vessel3.Server.Telemetry.Otlp;

namespace Vessel3.Server.Hosting;

internal static class OtelServiceExtensions
{
    public static IServiceCollection AddVesselOpenTelemetry(this IServiceCollection services, VesselConfig config)
    {
        if (config.Otel is { Enabled: true } otel && !string.IsNullOrWhiteSpace(otel.Endpoint))
        {
            services.AddSingleton(otel);
            services.AddSingleton<OtlpTraceExporter>(sp => new OtlpTraceExporter(
                otel,
                new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) },
                sp.GetRequiredService<ILogger<OtlpTraceExporter>>()));
            services.AddHostedService(sp => sp.GetRequiredService<OtlpTraceExporter>());
        }

        return services;
    }
}
