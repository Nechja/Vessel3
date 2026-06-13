using Amazon.Runtime;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Vessel3.UI;

internal sealed class BlazorHttpClientFactory : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(new BrowserStreamingHandler { InnerHandler = new HttpClientHandler() });

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => true;
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    public override string GetConfigUniqueString(IClientConfig clientConfig) => "blazor";
}

internal sealed class BrowserStreamingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserResponseStreamingEnabled(false);
        return base.SendAsync(request, cancellationToken);
    }
}
