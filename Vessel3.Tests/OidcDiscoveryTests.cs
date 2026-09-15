using System.Net;
using System.Text;
using Vessel3.Server.Oidc;
using Xunit;

namespace Vessel3.Tests;

public class OidcDiscoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Hits { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OidcOptions Options() =>
        OidcOptions.From("https://id.example.test", "vessel3", null, null).Match(o => o!, e => throw new InvalidOperationException(e.Message));

    private const string Document = """
        {"issuer":"https://id.example.test","authorization_endpoint":"https://id.example.test/authorize","token_endpoint":"https://id.example.test/token","jwks_uri":"https://id.example.test/jwks","end_session_endpoint":"https://id.example.test/end-session"}
        """;

    [Fact]
    public async Task Returns_endpoints_and_caches_them()
    {
        var handler = new StubHandler(() => Json(Document));
        using var discovery = new OidcDiscovery(Options(), new HttpClient(handler), new TestClock(T0));

        var doc = await discovery.Get(CancellationToken.None);
        await discovery.Get(CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal("https://id.example.test/authorize", doc.AuthorizationEndpoint);
        Assert.Equal("https://id.example.test/token", doc.TokenEndpoint);
        Assert.Equal("https://id.example.test/jwks", doc.JwksUri);
        Assert.Equal("https://id.example.test/end-session", doc.EndSessionEndpoint);
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task Refetches_after_max_age()
    {
        var handler = new StubHandler(() => Json(Document));
        var clock = new TestClock(T0);
        using var discovery = new OidcDiscovery(Options(), new HttpClient(handler), clock);

        await discovery.Get(CancellationToken.None);
        clock.Now = T0 + TimeSpan.FromHours(2);
        await discovery.Get(CancellationToken.None);

        Assert.Equal(2, handler.Hits);
    }

    [Fact]
    public async Task Outage_yields_null_then_recovers_after_retry_interval()
    {
        var up = false;
        var handler = new StubHandler(() => up ? Json(Document) : throw new HttpRequestException("down"));
        var clock = new TestClock(T0);
        using var discovery = new OidcDiscovery(Options(), new HttpClient(handler), clock);

        Assert.Null(await discovery.Get(CancellationToken.None));
        Assert.Null(await discovery.Get(CancellationToken.None));
        Assert.Equal(1, handler.Hits);

        up = true;
        clock.Now = T0 + TimeSpan.FromSeconds(30);
        Assert.NotNull(await discovery.Get(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_optional_endpoints_are_null()
    {
        var handler = new StubHandler(() => Json("""{"issuer":"https://id.example.test","jwks_uri":"https://id.example.test/jwks"}"""));
        using var discovery = new OidcDiscovery(Options(), new HttpClient(handler), new TestClock(T0));
        var doc = await discovery.Get(CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Null(doc.AuthorizationEndpoint);
        Assert.Null(doc.EndSessionEndpoint);
        Assert.Equal("https://id.example.test/jwks", doc.JwksUri);
    }
}
