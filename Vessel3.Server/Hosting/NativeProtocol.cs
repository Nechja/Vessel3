using Vessel3.Protocols.Native;
using Vessel3.Protocols.Native.Endpoints;
using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Storage;

namespace Vessel3.Server.Hosting;

internal sealed class NativeProtocol : IVesselProtocol
{
    public string Name => "Native";

    public void ConfigureServices(IServiceCollection services, VesselConfig config)
    {
        var isUnauthenticated = config.AccessKey is null && config.SecretKey is null && config.Oidc is null;
        var options = new NativeAuthOptions(isUnauthenticated, config.AccessKey, config.SecretKey);
        services.AddSingleton(options);

        if (config.Oidc is not null)
        {
            services.AddSingleton<ITokenAuthenticator, OidcTokenAuthenticator>();
        }

        services.AddSingleton<NativeAuthMiddleware>();
    }

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config)
    {
        app.UseMiddleware<NativeAuthMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapBucketEndpoints();
        endpoints.MapObjectEndpoints();
        endpoints.MapIdentityEndpoints();
        endpoints.MapAdminEndpoints();
        endpoints.MapWebhookEndpoints();
    }
}
