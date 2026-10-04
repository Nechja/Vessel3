using Xunit;

namespace Vessel3.Tests;

public class TokenVerifierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TestIssuer idp = new();

    private sealed class StaticKeys(params SigningKey[] keys) : ISigningKeys
    {
        public int Lookups { get; private set; }
        public Task<SigningKey?> Find(string kid, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult(keys.FirstOrDefault(k => k.Kid == kid));
        }
    }

    private TokenVerifier Verifier(string? audience = null, string? claim = null, TimeProvider? clock = null, ISigningKeys? keys = null) =>
        new(OidcOptions.From(idp.Issuer, "vessel3", audience, claim).Match(o => o!, e => throw new InvalidOperationException(e.Message)),
            keys ?? new StaticKeys(idp.Keys()), clock ?? new TestClock(T0));

    private static async Task<Error> ErrorOf(TokenVerifier v, string token)
    {
        Assert.False((await v.Verify(token, CancellationToken.None)).TryGetValue(out _, out var err));
        return err;
    }

    [Fact]
    public async Task Verify_Es256IdToken_Accepts()
    {
        var token = idp.Token(idp.Claims(T0));
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out var id, out _));
        Assert.Equal("acct_kayla", id.Subject);
        Assert.Equal(["vessel3"], id.Audiences);
    }

    [Fact]
    public async Task Verify_Rs256IdToken_Accepts()
    {
        var token = idp.Token(idp.Claims(T0), alg: "RS256");
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Theory]
    [InlineData("ES384")]
    [InlineData("ES512")]
    [InlineData("RS384")]
    [InlineData("RS512")]
    [InlineData("PS256")]
    [InlineData("PS384")]
    [InlineData("PS512")]
    public async Task Verify_SupportedAlgorithms_AcceptsAll(string alg)
    {
        var token = idp.Token(idp.Claims(T0), alg: alg);
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_MismatchedKeyCurve_Rejects()
    {
        var token = idp.Token(idp.Claims(T0), alg: "ES256", kid: idp.Ec384Kid);
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(), token));
    }

    [Fact]
    public async Task Verify_UndeclaredAlgorithm_Rejects()
    {
        var pinned = new StaticKeys(idp.RsaKey(declaredAlg: "RS256"));
        var ok = idp.Token(idp.Claims(T0), alg: "RS256");
        Assert.True((await Verifier(keys: pinned).Verify(ok, CancellationToken.None)).TryGetValue(out _, out _));
        var wrong = idp.Token(idp.Claims(T0), alg: "PS256");
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(keys: pinned), wrong));
    }

    [Fact]
    public async Task Verify_SymmetricHs256_Rejects()
    {
        var token = idp.Token(idp.Claims(T0), alg: "HS256", kid: idp.EcKid);
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(), token));
    }

    [Fact]
    public async Task Verify_JwtAccessTokenType_Accepts()
    {
        var token = idp.Token(idp.Claims(T0), typ: "at+jwt");
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_AudienceArrayContainingClient_Accepts()
    {
        var token = idp.Token(idp.Claims(T0, aud: "[\"vessel3\",\"shared\"]"));
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out var id, out _));
        Assert.Equal(["vessel3", "shared"], id.Audiences);
    }

    [Fact]
    public async Task Verify_ConfiguredAudienceWithoutClientId_Accepts()
    {
        var token = idp.Token(idp.Claims(T0, aud: "\"shared\""));
        Assert.True((await Verifier(audience: "shared").Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_ClientCredentialsToken_AcceptsClientIdAsSubject()
    {
        var claims = $$"""{"iss":"{{idp.Issuer}}","aud":"vessel3","client_id":"agent","iat":{{T0.ToUnixTimeSeconds()}},"exp":{{T0.ToUnixTimeSeconds() + 60}}}""";
        var token = idp.Token(claims, typ: "at+jwt");
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out var id, out _));
        Assert.Equal("agent", id.Subject);
    }

    [Fact]
    public async Task Verify_WrongIssuer_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0, iss: "https://other.test")));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_IssuerWithTrailingSlash_Accepts()
    {
        var token = idp.Token(idp.Claims(T0, iss: idp.Issuer + "/"));
        Assert.True((await Verifier().Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_WrongAudience_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0, aud: "\"grafana\"")));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_ExpiredToken_Rejects()
    {
        var err = await ErrorOf(Verifier(clock: new TestClock(T0 + TimeSpan.FromMinutes(10))), idp.Token(idp.Claims(T0)));
        Assert.IsType<ExpiredIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_LeewayOnExpiry_Accepts()
    {
        var token = idp.Token(idp.Claims(T0, ttlSeconds: 60));
        var v = Verifier(clock: new TestClock(T0 + TimeSpan.FromSeconds(100)));
        Assert.True((await v.Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_FutureToken_Rejects()
    {
        var nbf = (T0 + TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0, extra: $"\"nbf\":{nbf}")));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_TamperedSignature_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0), corrupt: true));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_AlgorithmNone_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0), alg: "none"));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_UnknownKid_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0), kid: "rotated"));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_AlgorithmMismatchingKey_Rejects()
    {
        using var other = new TestIssuer();
        var token = other.Token(other.Claims(T0), alg: "RS256", kid: idp.EcKid);
        var err = await ErrorOf(Verifier(), token);
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_UnknownTyp_Rejects()
    {
        var err = await ErrorOf(Verifier(), idp.Token(idp.Claims(T0), typ: "secevent+jwt"));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_MalformedToken_Rejects()
    {
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(), "not.a.jwt.at.all"));
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(), "a.b"));
        Assert.IsType<InvalidIdentityTokenError>(await ErrorOf(Verifier(), "!!!.!!!.!!!"));
    }

    [Fact]
    public async Task Verify_MissingSubject_Rejects()
    {
        var claims = $$"""{"iss":"{{idp.Issuer}}","aud":"vessel3","exp":{{T0.ToUnixTimeSeconds() + 60}}}""";
        var err = await ErrorOf(Verifier(), idp.Token(claims));
        Assert.IsType<InvalidIdentityTokenError>(err);
    }

    [Fact]
    public async Task Verify_RequiredClaimStringValue_Matches()
    {
        var token = idp.Token(idp.Claims(T0, extra: "\"role\":\"admin\""));
        Assert.True((await Verifier(claim: "role=admin").Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_RequiredClaimArrayMember_Matches()
    {
        var token = idp.Token(idp.Claims(T0, extra: "\"groups\":[\"staff\",\"vessel3\"]"));
        Assert.True((await Verifier(claim: "groups=vessel3").Verify(token, CancellationToken.None)).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Verify_MissingRequiredClaim_ReturnsAccessDenied()
    {
        var err = await ErrorOf(Verifier(claim: "groups=vessel3"), idp.Token(idp.Claims(T0, extra: "\"groups\":[\"staff\"]")));
        Assert.IsType<AccessDeniedError>(err);
        Assert.Equal(403, err.Status);
    }

    [Fact]
    public async Task Verify_AbsentRequiredClaim_ReturnsAccessDenied()
    {
        var err = await ErrorOf(Verifier(claim: "groups=vessel3"), idp.Token(idp.Claims(T0)));
        Assert.IsType<AccessDeniedError>(err);
    }

    [Fact]
    public async Task Verify_AdminUsersSubstringMatch_SetsIsAdmin()
    {
        var token = idp.Token(idp.Claims(T0));
        var verifier = new TokenVerifier(
            OidcOptions.From(idp.Issuer, "vessel3", null, null, null, ["kayla"]).Match(o => o!, e => throw new InvalidOperationException(e.Message)),
            new StaticKeys(idp.Keys()),
            new TestClock(T0));

        var result = await verifier.Verify(token, CancellationToken.None);
        Assert.True(result.TryGetValue(out var id, out _));
        Assert.True(id.IsAdmin);
    }

    [Fact]
    public async Task Verify_AdminClaim_SetsIsAdmin()
    {
        var token = idp.Token(idp.Claims(T0, extra: "\"roles\":[\"vessel-admin\"]"));
        var verifier = new TokenVerifier(
            OidcOptions.From(idp.Issuer, "vessel3", null, null, "roles=vessel-admin", null).Match(o => o!, e => throw new InvalidOperationException(e.Message)),
            new StaticKeys(idp.Keys()),
            new TestClock(T0));

        var result = await verifier.Verify(token, CancellationToken.None);
        Assert.True(result.TryGetValue(out var id, out _));
        Assert.True(id.IsAdmin);
    }

    public void Dispose() => idp.Dispose();
}
