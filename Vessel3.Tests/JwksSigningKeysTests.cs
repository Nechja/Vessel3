using System.Net;
using System.Text;
using Vessel3.Server.Oidc;
using Xunit;

namespace Vessel3.Tests;

public class JwksSigningKeysTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TestIssuer idp = new();

    private sealed class StubHandler(Func<string> jwks) : HttpMessageHandler
    {
        public int DiscoveryHits { get; private set; }
        public int JwksHits { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                DiscoveryHits++;
                var body = $$"""{"issuer":"https://id.example.test","jwks_uri":"https://id.example.test/.well-known/jwks.json"}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            if (url.EndsWith("/jwks.json", StringComparison.Ordinal))
            {
                JwksHits++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks(), Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static OidcOptions Options() =>
        OidcOptions.From("https://id.example.test", "vessel3", null, null).Match(o => o!, e => throw new InvalidOperationException(e.Message));

    private static JwksSigningKeys Keys(HttpMessageHandler handler, TimeProvider clock)
    {
        var http = new HttpClient(handler);
        return new JwksSigningKeys(new OidcDiscovery(Options(), http, clock), http, clock);
    }

    [Fact]
    public async Task Fetches_discovery_then_jwks_on_first_lookup()
    {
        var handler = new StubHandler(idp.Jwks);
        using var keys = Keys(handler, new TestClock(T0));

        var key = await keys.Find(idp.EcKid, CancellationToken.None);

        Assert.NotNull(key);
        Assert.True(key.Supports("ES256"));
        Assert.False(key.Supports("ES384"));
        Assert.Equal(1, handler.DiscoveryHits);
        Assert.Equal(1, handler.JwksHits);
    }

    [Fact]
    public async Task Parses_rsa_keys_too()
    {
        using var keys = Keys(new StubHandler(idp.Jwks), new TestClock(T0));
        var key = await keys.Find(idp.RsaKid, CancellationToken.None);
        Assert.NotNull(key);
        Assert.True(key.Supports("RS256"));
        Assert.True(key.Supports("PS512"));
        Assert.False(key.Supports("ES256"));
    }

    [Fact]
    public async Task Cached_key_does_not_refetch()
    {
        var handler = new StubHandler(idp.Jwks);
        using var keys = Keys(handler, new TestClock(T0));

        await keys.Find(idp.EcKid, CancellationToken.None);
        await keys.Find(idp.EcKid, CancellationToken.None);

        Assert.Equal(1, handler.JwksHits);
    }

    [Fact]
    public async Task Unknown_kid_refetches_after_the_refresh_interval()
    {
        var published = idp.Jwks();
        var handler = new StubHandler(() => published);
        var clock = new TestClock(T0);
        using var keys = Keys(handler, clock);

        Assert.NotNull(await keys.Find(idp.EcKid, CancellationToken.None));
        Assert.Null(await keys.Find("rotated", CancellationToken.None));
        Assert.Equal(1, handler.JwksHits);

        using var rotated = new TestIssuer();
        published = rotated.Jwks().Replace(rotated.EcKid, "rotated", StringComparison.Ordinal);
        clock.Now = T0 + TimeSpan.FromMinutes(2);

        Assert.NotNull(await keys.Find("rotated", CancellationToken.None));
        Assert.Equal(2, handler.JwksHits);
    }

    [Fact]
    public async Task Idp_outage_yields_no_key_rather_than_throwing()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("down"));
        using var keys = Keys(handler, new TestClock(T0));
        Assert.Null(await keys.Find(idp.EcKid, CancellationToken.None));
    }

    [Fact]
    public void ParseJwks_skips_unusable_entries()
    {
        var json = """{"keys":[{"kty":"EC","kid":"nocoords","crv":"P-256"},{"kty":"oct","kid":"sym","k":"abc"},{"kty":"EC","kid":"enc","use":"enc","crv":"P-256","x":"AA","y":"AA"},{"kty":"OKP","kid":"ed","crv":"Ed25519","x":"AA"},{"kty":"EC","kid":"odd","alg":"EdDSA","crv":"P-256","x":"AA","y":"AA"}]}""";
        Assert.Empty(SigningKey.ParseJwks(Encoding.UTF8.GetBytes(json)));
        Assert.Empty(SigningKey.ParseJwks(Encoding.UTF8.GetBytes("{}")));
    }

    public void Dispose() => idp.Dispose();
}
