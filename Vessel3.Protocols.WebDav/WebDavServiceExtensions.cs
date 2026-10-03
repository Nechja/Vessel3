using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Protocols.WebDav.Actions;
using Vessel3.Protocols.WebDav.Auth;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Middleware;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav;

public static class WebDavServiceExtensions
{
    public static IServiceCollection AddVesselWebDav(
        this IServiceCollection services,
        string? accessKey,
        string? secretKey)
    {
        services.AddSingleton<IWebDavXmlWriter, WebDavXmlWriter>();

        services.AddSingleton<IWebDavAuthenticator>(sp => new WebDavBasicAuthenticator(
            accessKey,
            secretKey,
            sp.GetService<IIdentityRegistry>()));

        services.AddSingleton<IWebDavAction, OptionsAction>();
        services.AddSingleton<IWebDavAction, PropfindAction>();
        services.AddSingleton<IWebDavAction, GetBlobAction>();
        services.AddSingleton<IWebDavAction, HeadBlobAction>();
        services.AddSingleton<IWebDavAction, PutBlobAction>();
        services.AddSingleton<IWebDavAction, DeleteAction>();
        services.AddSingleton<IWebDavAction, MkcolAction>();
        services.AddSingleton<IWebDavAction, MoveAction>();
        services.AddSingleton<IWebDavAction, CopyAction>();
        services.AddSingleton<IWebDavAction, LockAction>();
        services.AddSingleton<IWebDavAction, UnlockAction>();
        services.AddSingleton<IWebDavAction, ProppatchAction>();

        services.AddSingleton<IWebDavActionDispatcher, WebDavActionDispatcher>();
        services.AddSingleton<WebDavProtocolMiddleware>();

        return services;
    }

    public static IApplicationBuilder UseVesselWebDav(this IApplicationBuilder app) =>
        app.UseMiddleware<WebDavProtocolMiddleware>();
}
