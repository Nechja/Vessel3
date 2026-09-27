using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Vessel3.Client;
using Vessel3.UI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var bootHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var config = await bootHttp.GetFromJsonAsync<UiConfig>("config.json", jsonOpts)
    ?? throw new InvalidOperationException("config.json missing");

var auth = new UiAuth(config);
var notifications = new UiNotifications();
var origin = new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(notifications);
builder.Services.AddSingleton(sp => new Login(sp.GetRequiredService<IJSRuntime>(), sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(), config.Oidc));
builder.Services.AddSingleton<ObjectUrls>();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(origin + "/") });
builder.Services.AddTransient<IVesselClient>(sp =>
{
    var a = sp.GetRequiredService<UiAuth>();
    var http = sp.GetRequiredService<HttpClient>();
    return new VesselClient(http, new VesselClientOptions(origin, a.AccessKey, a.SecretKey, a.BearerToken));
});

var host = builder.Build();

if (config.Oidc is not null)
{
    var login = host.Services.GetRequiredService<Login>();
    try
    {
        var session = await login.Complete() ?? await login.Restore();
        if (session is not null)
        {
            auth.SignInBearer(session.BearerToken ?? "", session.Subject, session.Expires);
            login.ApplyPendingReturn();
        }
        else if (await login.SignedOut())
        {
            auth.SignedOut = true;
        }
        else
        {
            await login.Begin();
            return;
        }
    }
    catch (Exception e)
    {
        auth.Error = e.Message;
    }
}

try
{
    var client = host.Services.GetRequiredService<IVesselClient>();
    var whoResult = await client.WhoAmIAsync();
    if (whoResult.TryGetValue(out var caller, out _))
    {
        auth.Caller = caller;
    }
}
catch
{
}

await host.RunAsync();
