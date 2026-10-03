using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Protocols.Azure.Actions.Blob;
using Vessel3.Protocols.Azure.Actions.Container;
using Vessel3.Protocols.Azure.Actions.Service;
using Vessel3.Protocols.Azure.Auth;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Middleware;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure;

public static class AzureServiceExtensions
{
    public static IServiceCollection AddVesselAzure(
        this IServiceCollection services,
        string? accessKey,
        string? secretKey)
    {
        services.AddSingleton<IAzureErrorXmlWriter, AzureErrorXmlWriter>();
        services.AddSingleton<IAzureXmlWriter, AzureXmlWriter>();

        services.AddSingleton<IAzureVerifier>(sp => new AzureSharedKeyVerifier(
            accessKey,
            secretKey,
            sp.GetService<IIdentityRegistry>()));

        // Actions
        services.AddSingleton<IAzureAction, ListContainersAction>();
        services.AddSingleton<IAzureAction, GetServicePropertiesAction>();
        services.AddSingleton<IAzureAction, GetAccountInfoAction>();
        services.AddSingleton<IAzureAction, CreateContainerAction>();
        services.AddSingleton<IAzureAction, GetContainerPropertiesAction>();
        services.AddSingleton<IAzureAction, DeleteContainerAction>();
        services.AddSingleton<IAzureAction, ListBlobsAction>();
        services.AddSingleton<IAzureAction, PutBlobAction>();
        services.AddSingleton<IAzureAction, GetBlobAction>();
        services.AddSingleton<IAzureAction, HeadBlobAction>();
        services.AddSingleton<IAzureAction, DeleteBlobAction>();
        services.AddSingleton<IAzureAction, PutBlockAction>();
        services.AddSingleton<IAzureAction, PutBlockListAction>();

        services.AddSingleton<IAzureActionDispatcher, AzureActionDispatcher>();
        services.AddSingleton<AzureProtocolMiddleware>();

        return services;
    }

    public static IApplicationBuilder UseVesselAzure(this IApplicationBuilder app)
    {
        app.UseMiddleware<AzureProtocolMiddleware>();
        return app;
    }
}
