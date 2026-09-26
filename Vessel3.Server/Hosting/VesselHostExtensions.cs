namespace Vessel3.Server.Hosting;

internal static class VesselHostExtensions
{
    public static void ConfigureVesselHost(this WebApplicationBuilder builder)
    {
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 5L * 1024 * 1024 * 1024;
            options.Limits.MinRequestBodyDataRate = null;
        });
    }
}
