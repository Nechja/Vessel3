using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;
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

        if (config.Oidc is not null)
        {
            app.UseMiddleware<StsEndpointMiddleware>();
        }

        app.UseMiddleware<VirtualHostBucketMiddleware>();
        app.UseMiddleware<CorsAndAccessMiddleware>();
        app.UseMiddleware<WebsiteServingMiddleware>();
        app.UseMiddleware<SigV4Middleware>();
        app.UseMiddleware<VirtualHostS3DispatchMiddleware>();
    }
}
