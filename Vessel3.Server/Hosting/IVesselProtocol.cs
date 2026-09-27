using Vessel3.Server.Configuration;

namespace Vessel3.Server.Hosting;

internal interface IVesselProtocol
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services, VesselConfig config);
    void ConfigurePipeline(IApplicationBuilder app, VesselConfig config);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
