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
builder.Services.AddScoped(sp => new HttpClient(new SigV4Handler(auth, config.Region)
{
    InnerHandler = new SessionGuardHandler(auth, sp.GetRequiredService<Login>()) { InnerHandler = new HttpClientHandler() },
})
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
    try
    {
        if (oidc.AuthorizationEndpoint is null || oidc.TokenEndpoint is null)
            throw new LoginException($"identity provider {oidc.Issuer} is unreachable");
        var session = await login.Complete() ?? await login.Restore();
        if (session is not null)
        {
            auth.SignIn(session);
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

await host.RunAsync();
