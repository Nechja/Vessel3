using System.Net.Http.Json;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using Vessel3.UI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var bootHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var config = await bootHttp.GetFromJsonAsync<UiConfig>("config.json", jsonOpts)
    ?? throw new InvalidOperationException("config.json missing");
builder.Services.AddSingleton(config);

var origin = new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority);
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(config.AccessKey, config.SecretKey),
    new AmazonS3Config
    {
        ServiceURL = origin,
        ForcePathStyle = true,
        AuthenticationRegion = config.Region,
        HttpClientFactory = new BlazorHttpClientFactory(),
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = 1,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));

await builder.Build().RunAsync();
