using System.Net;
using System.Net.Http.Headers;

namespace Vessel3.UI;

internal sealed class SessionGuardHandler(UiAuth auth, Login login) : DelegatingHandler
{
    private static readonly string[] SessionLost = ["<Code>ExpiredToken</Code>", "<Code>InvalidToken</Code>", "<Code>InvalidAccessKeyId</Code>"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken);
        if (auth.Session is not null
            && resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
            && resp.Content.Headers.ContentType?.MediaType is "application/xml")
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (SessionLost.Any(body.Contains)) await login.Begin();
        }
        return resp;
    }
}

internal sealed class UiBearerHandler(UiAuth auth, Login login) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (auth.Session is { } s)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{s.AccessKey}:{s.Token}");
        var resp = await base.SendAsync(request, cancellationToken);
        if (auth.Session is not null && resp.StatusCode == HttpStatusCode.Unauthorized) await login.Begin();
        return resp;
    }
}
