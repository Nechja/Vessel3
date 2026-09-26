using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;

if (!VesselConfig.TryCreate(out var config, out var error))
{
    Console.Error.WriteLine(error);
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.ConfigureVesselHost();
builder.Services.AddVessel(config);

var app = builder.Build();
app.UseVesselPipeline(config);
app.MapVesselEndpoints();

app.Run();
return 0;
