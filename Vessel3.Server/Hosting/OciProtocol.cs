using Vessel3.Protocols.Oci;
using Vessel3.Server.Configuration;

namespace Vessel3.Server.Hosting;

internal sealed class OciProtocol : IVesselProtocol
{
    public string Name => "OCI";

    public void ConfigureServices(IServiceCollection services, VesselConfig config)
    {
        services.AddContainerRepos(config.DataRoot, config.AccessKey, config.SecretKey);
    }

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config)
    {
        app.UseContainerRepos();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapContainerRepoEndpoints();
    }
}
