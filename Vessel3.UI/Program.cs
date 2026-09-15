using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Vessel3.UI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

var bootHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var config = await bootHttp.GetFromJsonAsync<UiConfig>("config.json", jsonOpts)
    ?? throw new InvalidOperationException("config.json missing");
var auth = new UiAuth(config);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(sp => new Login(sp.GetRequiredService<IJSRuntime>(), sp.GetRequiredService<NavigationManager>(), config.Oidc));
builder.Services.AddScoped(sp => new HttpClient(new UiBearerHandler(auth, sp.GetRequiredService<Login>()) { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<ObjectUrls>();

var origin = new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority);
builder.Services.AddSingleton<IAmazonS3>(sp => new AmazonS3Client(
    auth.Credentials,
    new AmazonS3Config
    {
        ServiceURL = origin,
        ForcePathStyle = true,
        AuthenticationRegion = config.Region,
        HttpClientFactory = new BlazorHttpClientFactory(auth, sp.GetRequiredService<Login>()),
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = 1,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));

var host = builder.Build();

if (config.Oidc is { } oidc)
{
    var login = host.Services.GetRequiredService<Login>();
    if (oidc.AuthorizationEndpoint is null || oidc.TokenEndpoint is null)
    {
        auth.Error = $"identity provider {oidc.Issuer} is unreachable";
    }
    else
    {
        try
        {
            if (await login.Complete()) return;
            var session = await login.Restore();
            if (session is null)
            {
                await login.Begin();
                return;
            }
            auth.SignIn(session);
        }
        catch (Exception e) when (e is LoginException or HttpRequestException)
        {
            auth.Error = e.Message;
        }
    }
}

await host.RunAsync();
