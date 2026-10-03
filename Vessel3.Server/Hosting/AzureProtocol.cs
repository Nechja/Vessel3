using Vessel3.Protocols.Azure;
using Vessel3.Server.Configuration;

namespace Vessel3.Server.Hosting;

internal sealed class AzureProtocol : IVesselProtocol
{
    public string Name => "Azure";

    public void ConfigureServices(IServiceCollection services, VesselConfig config) =>
        services.AddVesselAzure(config.AccessKey, config.SecretKey);

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config) =>
        app.UseVesselAzure();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
