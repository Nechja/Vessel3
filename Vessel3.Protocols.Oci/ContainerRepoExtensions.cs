using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Protocols.Oci.Actions;
using Vessel3.Protocols.Oci.Actions.Blobs;
using Vessel3.Protocols.Oci.Actions.Manifests;
using Vessel3.Protocols.Oci.Dispatch;
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
        services.AddSingleton<IOciAction, PingAction>();
        services.AddSingleton<IOciAction, TokenAction>();
        services.AddSingleton<IOciAction, CatalogAction>();
        services.AddSingleton<IOciAction, TagsAction>();
        services.AddSingleton<IOciAction, HeadBlobAction>();
        services.AddSingleton<IOciAction, GetBlobAction>();
        services.AddSingleton<IOciAction, StartBlobUploadAction>();
        services.AddSingleton<IOciAction, AppendBlobUploadChunkAction>();
        services.AddSingleton<IOciAction, CommitBlobUploadAction>();
        services.AddSingleton<IOciAction, GetBlobUploadStatusAction>();
        services.AddSingleton<IOciAction, CancelBlobUploadAction>();
        services.AddSingleton<IOciAction, HeadManifestAction>();
        services.AddSingleton<IOciAction, GetManifestAction>();
        services.AddSingleton<IOciAction, PutManifestAction>();
        services.AddSingleton<IOciAction, DeleteManifestAction>();
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
