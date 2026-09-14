using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class SigV4VerifierTests
{
    private const string Region = "us-east-1";
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Credential Root = new("AKIAROOT", "rootsecret", null, null);
    private static readonly Credential Session = new("ASIASESSION00000001", "sessionsecret", "tok3nAbC123", T0 + TimeSpan.FromHours(1));

    private sealed class FakeStore(params Credential[] creds) : ICredentialStore
    {
        public Credential? Find(string accessKey) => creds.FirstOrDefault(c => c.AccessKey == accessKey);
        public Credential IssueSession(string subject, TimeSpan ttl) => throw new NotSupportedException();
    }

    private static SigV4Verifier Verifier(TimeProvider? clock = null, params Credential[] creds) =>
        new(new FakeStore(creds), new ServerRegion(Region), clock ?? new TestClock(T0));

    [Fact]
    public void Header_auth_accepts_root_credential()
    {
        var req = SignedHeader(Root, T0.UtcDateTime);
        Assert.True(Verifier(null, Root).Verify(req).TryGetValue(out var ctx, out _));
        Assert.Equal(T0.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture), ctx.AmzDate);
    }

    [Fact]
    public void Header_auth_rejects_unknown_access_key()
    {
        var req = SignedHeader(new Credential("AKIANOPE", "x", null, null), T0.UtcDateTime);
        Assert.False(Verifier(null, Root).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidAccessKeyIdError>(err);
    }

    [Fact]
    public void Header_auth_rejects_wrong_secret()
    {
        var req = SignedHeader(Root with { Secret = "wrong" }, T0.UtcDateTime);
        Assert.False(Verifier(null, Root).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<SignatureDoesNotMatchError>(err);
    }

    [Fact]
    public void Header_auth_accepts_session_credential_with_signed_token()
    {
        var req = SignedHeader(Session, T0.UtcDateTime);
        Assert.True(Verifier(null, Root, Session).Verify(req).TryGetValue(out _, out _));
    }

    [Fact]
    public void Header_auth_rejects_session_credential_without_token()
    {
        var req = SignedHeader(Session with { SessionToken = null }, T0.UtcDateTime);
        Assert.False(Verifier(null, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidTokenError>(err);
    }

    [Fact]
    public void Header_auth_rejects_session_credential_with_wrong_token()
    {
        var req = SignedHeader(Session with { SessionToken = "other" }, T0.UtcDateTime);
        Assert.False(Verifier(null, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidTokenError>(err);
    }

    [Fact]
    public void Header_auth_rejects_unsigned_token_header()
    {
        var req = SignedHeader(Session, T0.UtcDateTime, signToken: false);
        Assert.False(Verifier(null, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidTokenError>(err);
    }

    [Fact]
    public void Header_auth_rejects_token_on_root_credential()
    {
        var req = SignedHeader(Root with { SessionToken = "stray" }, T0.UtcDateTime);
        Assert.False(Verifier(null, Root).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidTokenError>(err);
    }

    [Fact]
    public void Header_auth_rejects_expired_session()
    {
        var later = new TestClock(T0 + TimeSpan.FromHours(1));
        var req = SignedHeader(Session, later.Now.UtcDateTime);
        Assert.False(Verifier(later, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<ExpiredTokenError>(err);
    }

    [Fact]
    public void Header_auth_rejects_skewed_timestamp()
    {
        var req = SignedHeader(Root, T0.UtcDateTime - TimeSpan.FromMinutes(16));
        Assert.False(Verifier(null, Root).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<RequestTimeTooSkewedError>(err);
    }

    [Fact]
    public void Presigned_accepts_root_credential()
    {
        var req = Presigned(Root, T0.UtcDateTime, expires: 300);
        Assert.True(Verifier(null, Root).Verify(req).TryGetValue(out _, out _));
    }

    [Fact]
    public void Presigned_accepts_session_credential_with_token()
    {
        var req = Presigned(Session, T0.UtcDateTime, expires: 300);
        Assert.True(Verifier(null, Session).Verify(req).TryGetValue(out _, out _));
    }

    [Fact]
    public void Presigned_rejects_session_credential_without_token()
    {
        var req = Presigned(Session with { SessionToken = null }, T0.UtcDateTime, expires: 300);
        Assert.False(Verifier(null, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<InvalidTokenError>(err);
    }

    [Fact]
    public void Presigned_rejects_expired_session_even_before_url_expiry()
    {
        var later = new TestClock(T0 + TimeSpan.FromHours(1));
        var req = Presigned(Session, later.Now.UtcDateTime, expires: 3600);
        Assert.False(Verifier(later, Session).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<ExpiredTokenError>(err);
    }

    [Fact]
    public void Presigned_rejects_expired_url()
    {
        var later = new TestClock(T0 + TimeSpan.FromMinutes(10));
        var req = Presigned(Root, T0.UtcDateTime, expires: 300);
        Assert.False(Verifier(later, Root).Verify(req).TryGetValue(out _, out var err));
        Assert.IsType<RequestTimeTooSkewedError>(err);
    }

    private static HttpRequest SignedHeader(Credential cred, DateTime now, bool signToken = true)
    {
        var req = new DefaultHttpContext().Request;
        req.Method = "GET";
        req.Path = "/bucket/key";
        req.Headers.Host = "localhost";

        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        req.Headers["x-amz-date"] = amzDate;
        req.Headers["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD";

        var signed = new List<string> { "host", "x-amz-content-sha256", "x-amz-date" };
        if (cred.SessionToken is not null)
        {
            req.Headers["x-amz-security-token"] = cred.SessionToken;
            if (signToken) signed.Add("x-amz-security-token");
        }
        signed.Sort(StringComparer.Ordinal);

        var canonical = new StringBuilder()
            .Append("GET\n/bucket/key\n\n");
        foreach (var h in signed) canonical.Append(h).Append(':').Append(req.Headers[h].ToString()).Append('\n');
        canonical.Append('\n').Append(string.Join(';', signed)).Append("\nUNSIGNED-PAYLOAD");

        var scope = $"{date}/{Region}/s3/aws4_request";
        var signature = Sign(cred.Secret, date, amzDate, scope, canonical.ToString());
        req.Headers.Authorization =
            $"AWS4-HMAC-SHA256 Credential={cred.AccessKey}/{scope}, SignedHeaders={string.Join(';', signed)}, Signature={signature}";
        return req;
    }

    private static HttpRequest Presigned(Credential cred, DateTime now, int expires)
    {
        var req = new DefaultHttpContext().Request;
        req.Method = "GET";
        req.Path = "/bucket/key";
        req.Headers.Host = "localhost";

        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var scope = $"{date}/{Region}/s3/aws4_request";

        var query = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{cred.AccessKey}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expires.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };
        if (cred.SessionToken is not null) query["X-Amz-Security-Token"] = cred.SessionToken;

        var canonicalQuery = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var canonical = $"GET\n/bucket/key\n{canonicalQuery}\nhost:localhost\n\nhost\nUNSIGNED-PAYLOAD";
        query["X-Amz-Signature"] = Sign(cred.Secret, date, amzDate, scope, canonical);

        req.QueryString = QueryString.Create(query);
        return req;
    }

    private static string Sign(string secret, string date, string amzDate, string scope, string canonical)
    {
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(date));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(Region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));
    }
}
