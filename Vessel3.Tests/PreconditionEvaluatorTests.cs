using Microsoft.AspNetCore.Http;
using Vessel3.Server.S3;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class PreconditionEvaluatorTests
{
    private static readonly DateTimeOffset Modified = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static PreconditionRules R(params (string K, string V)[] kv)
    {
        var h = new HeaderDictionary();
        foreach (var (k, v) in kv) h[k] = v;
        return S3HeaderCodec.ExtractReadPreconditions(h);
    }

    private static WritePreconditions W(params (string K, string V)[] kv)
    {
        var h = new HeaderDictionary();
        foreach (var (k, v) in kv) h[k] = v;
        return S3HeaderCodec.ExtractWritePreconditions(h);
    }

    [Fact]
    public void Read_NoHeaders_Pass()
    {
        var r = new PreconditionEvaluator().Evaluate(R(), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateRead_IfMatchExact_Passes()
    {
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Match", "\"abc123\"")), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateRead_IfMatchWildcard_Passes()
    {
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Match", "*")), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateRead_IfMatchMismatch_Fails()
    {
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Match", "\"other\"")), "abc123", Modified);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void EvaluateRead_IfNoneMatchHit_ReturnsNotModified()
    {
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-None-Match", "\"abc123\"")), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void EvaluateRead_IfNoneMatchStarHit_ReturnsNotModified()
    {
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-None-Match", "*")), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void EvaluateRead_IfModifiedSinceOlder_ReturnsNotModified()
    {
        var ims = Modified.AddSeconds(60).ToString("R");
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Modified-Since", ims)), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void EvaluateRead_IfModifiedSinceNewer_Passes()
    {
        var ims = Modified.AddSeconds(-60).ToString("R");
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Modified-Since", ims)), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateRead_IfUnmodifiedSinceOlder_Fails()
    {
        var ius = Modified.AddSeconds(-60).ToString("R");
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-Unmodified-Since", ius)), "abc123", Modified);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void EvaluateRead_IfNoneMatch_SuppressesIfModifiedSince()
    {
        var ims = Modified.AddSeconds(60).ToString("R");
        var r = new PreconditionEvaluator().Evaluate(
            R(("If-None-Match", "\"other\""), ("If-Modified-Since", ims)),
            "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void CopySourcePreconditions_ExtractedCorrectly()
    {
        var h = new HeaderDictionary
        {
            ["x-amz-copy-source-if-match"] = "\"etag1\"",
            ["x-amz-copy-source-if-none-match"] = "\"etag2\"",
            ["x-amz-copy-source-if-modified-since"] = "Wed, 21 Oct 2015 07:28:00 GMT",
            ["x-amz-copy-source-if-unmodified-since"] = "Wed, 21 Oct 2015 07:28:00 GMT"
        };
        var rules = S3HeaderCodec.ExtractCopySourcePreconditions(h);
        Assert.Equal("\"etag1\"", rules.IfMatch);
        Assert.Equal("\"etag2\"", rules.IfNoneMatch);
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", rules.IfModifiedSince);
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", rules.IfUnmodifiedSince);
    }

    [Fact]
    public void Write_NoConditions_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(W(), currentEtag: "abc");
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateWrite_IfNoneMatchStarNoCurrent_Passes()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            W(("If-None-Match", "*")), currentEtag: null);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void EvaluateWrite_IfNoneMatchStarExisting_Fails()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            W(("If-None-Match", "*")), currentEtag: "abc");
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void EvaluateWrite_IfMatchNoCurrent_Fails()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            W(("If-Match", "\"abc\"")), currentEtag: null);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void EvaluateWrite_IfMatchMatches_Passes()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            W(("If-Match", "\"abc\"")), currentEtag: "abc");
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void HasWriteConditions_NoneByDefault()
    {
        Assert.False(new PreconditionEvaluator().HasWriteConditions(W()));
    }

    [Fact]
    public void HasWriteConditions_IfMatch()
    {
        Assert.True(new PreconditionEvaluator().HasWriteConditions(W(("If-Match", "\"x\""))));
    }
}
