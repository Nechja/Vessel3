using Vessel3.Protocols.WebDav;
using Vessel3.Server.Configuration;

namespace Vessel3.Server.Hosting;

internal sealed class WebDavProtocol : IVesselProtocol
{
    public string Name => "WebDAV";

    public void ConfigureServices(IServiceCollection services, VesselConfig config) =>
        services.AddVesselWebDav(config.AccessKey, config.SecretKey);

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config) =>
        app.UseVesselWebDav();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
