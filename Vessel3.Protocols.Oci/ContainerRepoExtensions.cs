using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci;

public static class ContainerRepoExtensions
{
    public static IServiceCollection AddContainerRepos(
        this IServiceCollection services,
        string dataRoot,
        string? accessKey,
        string? secretKey)
    {
        var ociRoot = Path.Combine(dataRoot, "oci");
        var catalogOptions = new ContainerRepoCatalogOptions(ociRoot);
        services.AddSingleton(catalogOptions);
        services.AddSingleton<IContainerRepoCatalog, SqliteContainerRepoCatalog>();
        services.AddSingleton<IBlobReferenceSource>(sp => sp.GetRequiredService<IContainerRepoCatalog>());

        var isUnauthenticated = string.IsNullOrEmpty(accessKey) && string.IsNullOrEmpty(secretKey);
        var authOptions = new ContainerRepoAuthOptions(isUnauthenticated, accessKey, secretKey);
        services.AddSingleton(authOptions);

        services.AddSingleton<IContainerRepoTokenService>(sp =>
            new ContainerRepoTokenService(secretKey, sp.GetService<TimeProvider>()));
        services.AddSingleton<ContainerRepoAuthMiddleware>(sp => new ContainerRepoAuthMiddleware(
            sp.GetRequiredService<IIdentityRegistry>(),
            sp.GetRequiredService<IContainerRepoTokenService>(),
            sp.GetRequiredService<ContainerRepoAuthOptions>(),
            sp.GetService<ITokenAuthenticator>()));
        services.AddSingleton<IOciDispatcher, OciDispatcher>();

        return services;
    }

    public static IApplicationBuilder UseContainerRepos(this IApplicationBuilder app) =>
        app.UseMiddleware<ContainerRepoAuthMiddleware>();

    public static IEndpointRouteBuilder MapContainerRepoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/v2", ["GET", "HEAD"], static (HttpContext ctx, IOciDispatcher dispatcher) =>
            dispatcher.Dispatch("", ctx)).WithOrder(0);

        endpoints.MapMethods("/v2/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"], static (string? path, HttpContext ctx, IOciDispatcher dispatcher) =>
            dispatcher.Dispatch(path ?? "", ctx)).WithOrder(1);

        return endpoints;
    }
}
