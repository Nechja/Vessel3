using Vessel3.Server.Configuration;
using Vessel3.Server.Hosting;
#if VESSEL3_UI
using Vessel3.Server.Ui;
#endif

namespace Vessel3.Server.Pipeline;

internal static class VesselPipelineExtensions
{
    public static void UseVesselPipeline(this WebApplication app, VesselConfig config)
    {
        app.UseMiddleware<MetricsEndpointMiddleware>();
        app.UseMiddleware<RequestTelemetry>();
        app.UseMiddleware<AdminHostRedirectMiddleware>();

#if VESSEL3_UI
        app.UseMiddleware<UiServingMiddleware>();
#endif

        foreach (var protocol in app.Services.GetServices<IVesselProtocol>())
        {
            protocol.ConfigurePipeline(app, config);
        }
    }
}
