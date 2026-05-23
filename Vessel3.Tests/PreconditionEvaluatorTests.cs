using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public class PreconditionEvaluatorTests
{
    private static readonly DateTimeOffset Modified = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static IHeaderDictionary H(params (string K, string V)[] kv)
    {
        var h = new HeaderDictionary();
        foreach (var (k, v) in kv) h[k] = v;
        return h;
    }

    [Fact]
    public void Read_NoHeaders_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(H(), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Read_IfMatch_Exact_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Match", "\"abc123\"")), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Read_IfMatch_Wildcard_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Match", "*")), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Read_IfMatch_Mismatch_Failed()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Match", "\"other\"")), "abc123", Modified);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void Read_IfNoneMatch_Hit_NotModified()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-None-Match", "\"abc123\"")), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void Read_IfNoneMatch_Star_Hit_NotModified()
    {
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-None-Match", "*")), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void Read_IfModifiedSince_Older_NotModified()
    {
        var ims = Modified.AddSeconds(60).ToString("R");
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Modified-Since", ims)), "abc123", Modified);
        Assert.Equal(Precondition.NotModified, r);
    }

    [Fact]
    public void Read_IfModifiedSince_Newer_Pass()
    {
        var ims = Modified.AddSeconds(-60).ToString("R");
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Modified-Since", ims)), "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Read_IfUnmodifiedSince_Older_Failed()
    {
        var ius = Modified.AddSeconds(-60).ToString("R");
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-Unmodified-Since", ius)), "abc123", Modified);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void Read_IfNoneMatch_Suppresses_IfModifiedSince()
    {
        // If-None-Match present + no match → 304 only if If-None-Match hit, ignore If-Modified-Since.
        var ims = Modified.AddSeconds(60).ToString("R");
        var r = new PreconditionEvaluator().EvaluateForRead(
            H(("If-None-Match", "\"other\""), ("If-Modified-Since", ims)),
            "abc123", Modified);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Write_NoConditions_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(H(), currentEtag: "abc");
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Write_IfNoneMatchStar_NoCurrent_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            H(("If-None-Match", "*")), currentEtag: null);
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void Write_IfNoneMatchStar_Existing_Failed()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            H(("If-None-Match", "*")), currentEtag: "abc");
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void Write_IfMatch_NoCurrent_Failed()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            H(("If-Match", "\"abc\"")), currentEtag: null);
        Assert.Equal(Precondition.Failed, r);
    }

    [Fact]
    public void Write_IfMatch_Match_Pass()
    {
        var r = new PreconditionEvaluator().EvaluateForWrite(
            H(("If-Match", "\"abc\"")), currentEtag: "abc");
        Assert.Equal(Precondition.Pass, r);
    }

    [Fact]
    public void HasWriteConditions_NoneByDefault()
    {
        Assert.False(new PreconditionEvaluator().HasWriteConditions(H()));
    }

    [Fact]
    public void HasWriteConditions_IfMatch()
    {
        Assert.True(new PreconditionEvaluator().HasWriteConditions(H(("If-Match", "\"x\""))));
    }
}
