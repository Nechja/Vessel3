using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Server;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class StsEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "https://sts.amazonaws.com/doc/2011-06-15/";

    private sealed class FakeVerifier(Result<VerifiedIdentity> result) : ITokenVerifier
    {
        public string? Seen { get; private set; }
        public Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct)
        {
            Seen = token;
            return Task.FromResult(result);
        }
    }

    private static (HttpContext Ctx, CredentialStore Store, FakeVerifier Verifier) Request(
        string body, Result<VerifiedIdentity>? outcome = null, string? query = null)
    {
        var store = new CredentialStore(null, new TestClock(T0));
        var verifier = new FakeVerifier(outcome ?? new VerifiedIdentity("acct_kayla", ["vessel3"]));
        var services = new ServiceCollection()
            .AddSingleton<ICredentialStore>(store)
            .AddSingleton<ITokenVerifier>(verifier)
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/";
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        if (body.Length > 0)
        {
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }
        ctx.Response.Body = new MemoryStream();
        return (ctx, store, verifier);
    }

    private static XDocument Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return XDocument.Load(ctx.Response.Body);
    }

    [Fact]
    public void Matches_only_post_to_root()
    {
        var post = new DefaultHttpContext().Request;
        post.Method = "POST";
        post.Path = "/";
        Assert.True(StsEndpoint.Matches(post));

        var get = new DefaultHttpContext().Request;
        get.Method = "GET";
        get.Path = "/";
        Assert.False(StsEndpoint.Matches(get));

        var bucket = new DefaultHttpContext().Request;
        bucket.Method = "POST";
        bucket.Path = "/photos";
        Assert.False(StsEndpoint.Matches(bucket));
    }

    [Fact]
    public async Task Issues_session_credentials_for_a_verified_token()
    {
        var (ctx, store, verifier) = Request("Action=AssumeRoleWithWebIdentity&Version=2011-06-15&WebIdentityToken=eyJ.abc.def&RoleArn=arn:aws:iam::0:role/x&RoleSessionName=ui");
        await StsEndpoint.Handle(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("eyJ.abc.def", verifier.Seen);
        var creds = Body(ctx).Root!.Element(Ns + "AssumeRoleWithWebIdentityResult")!.Element(Ns + "Credentials")!;
        var accessKey = creds.Element(Ns + "AccessKeyId")!.Value;
        var issued = store.Find(accessKey);
        Assert.NotNull(issued);
        Assert.Equal(issued.Secret, creds.Element(Ns + "SecretAccessKey")!.Value);
        Assert.Equal(issued.SessionToken, creds.Element(Ns + "SessionToken")!.Value);
        Assert.Equal("2026-09-13T13:00:00Z", creds.Element(Ns + "Expiration")!.Value);
        Assert.Equal("acct_kayla", issued.Subject);
        Assert.Equal("acct_kayla", Body(ctx).Root!.Element(Ns + "AssumeRoleWithWebIdentityResult")!.Element(Ns + "SubjectFromWebIdentityToken")!.Value);
    }

    [Fact]
    public async Task Honors_duration_seconds()
    {
        var (ctx, store, _) = Request("Action=AssumeRoleWithWebIdentity&WebIdentityToken=t&DurationSeconds=7200");
        await StsEndpoint.Handle(ctx);
        var accessKey = Body(ctx).Descendants(Ns + "AccessKeyId").Single().Value;
        Assert.Equal(T0 + TimeSpan.FromHours(2), store.Find(accessKey)!.ExpiresAt);
    }

    [Theory]
    [InlineData("60")]
    [InlineData("99999")]
    [InlineData("abc")]
    public async Task Rejects_out_of_range_duration(string duration)
    {
        var (ctx, _, _) = Request($"Action=AssumeRoleWithWebIdentity&WebIdentityToken=t&DurationSeconds={duration}");
        await StsEndpoint.Handle(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("ValidationError", Body(ctx).Descendants(Ns + "Code").Single().Value);
    }

    [Fact]
    public async Task Accepts_parameters_in_query_string()
    {
        var (ctx, _, verifier) = Request("", query: "?Action=AssumeRoleWithWebIdentity&WebIdentityToken=fromquery");
        await StsEndpoint.Handle(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("fromquery", verifier.Seen);
    }

    [Fact]
    public async Task Rejects_other_actions()
    {
        var (ctx, _, _) = Request("Action=GetCallerIdentity");
        await StsEndpoint.Handle(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("InvalidAction", Body(ctx).Descendants(Ns + "Code").Single().Value);
    }

    [Fact]
    public async Task Rejects_missing_token()
    {
        var (ctx, _, _) = Request("Action=AssumeRoleWithWebIdentity");
        await StsEndpoint.Handle(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("ValidationError", Body(ctx).Descendants(Ns + "Code").Single().Value);
    }

    [Fact]
    public async Task Maps_verifier_errors_to_sts_error_response()
    {
        var (ctx, store, _) = Request("Action=AssumeRoleWithWebIdentity&WebIdentityToken=bad", outcome: new ExpiredIdentityTokenError());
        await StsEndpoint.Handle(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        var error = Body(ctx).Root!.Element(Ns + "Error")!;
        Assert.Equal("Sender", error.Element(Ns + "Type")!.Value);
        Assert.Equal("ExpiredTokenException", error.Element(Ns + "Code")!.Value);
    }

    [Fact]
    public async Task Access_denied_is_403()
    {
        var (ctx, _, _) = Request("Action=AssumeRoleWithWebIdentity&WebIdentityToken=t", outcome: new AccessDeniedError("nope"));
        await StsEndpoint.Handle(ctx);
        Assert.Equal(403, ctx.Response.StatusCode);
        Assert.Equal("AccessDenied", Body(ctx).Descendants(Ns + "Code").Single().Value);
    }
}
