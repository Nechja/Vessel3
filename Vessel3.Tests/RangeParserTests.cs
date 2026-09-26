using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class RangeParserTests
{
    [Fact]
    public void Normal_ClosedRange()
    {
        var r = S3ByteRange.Parse("bytes=0-9", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(9, n.End);
    }

    [Fact]
    public void Normal_MidRange()
    {
        var r = S3ByteRange.Parse("bytes=10-19", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(10, n.Start);
        Assert.Equal(19, n.End);
    }

    [Fact]
    public void OpenEnd_ServesToEnd()
    {
        var r = S3ByteRange.Parse("bytes=50-", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(50, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void OpenEnd_Zero_FullObject()
    {
        var r = S3ByteRange.Parse("bytes=0-", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_LastNBytes()
    {
        var r = S3ByteRange.Parse("bytes=-10", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(90, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_LargerThanSize_ClampsToFullObject()
    {
        var r = S3ByteRange.Parse("bytes=-9999", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_Zero_Unsatisfiable()
    {
        var r = S3ByteRange.Parse("bytes=-0", 100);
        Assert.IsType<S3ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void StartAtSize_Unsatisfiable()
    {
        var r = S3ByteRange.Parse("bytes=100-200", 100);
        Assert.IsType<S3ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void StartBeyondSize_Unsatisfiable()
    {
        var r = S3ByteRange.Parse("bytes=500-", 100);
        Assert.IsType<S3ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void EndPastSize_ClampedToLastByte()
    {
        var r = S3ByteRange.Parse("bytes=50-9999", 100);
        var n = Assert.IsType<S3ByteRange.Normal>(r);
        Assert.Equal(50, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void MultiRange_Ignored()
    {
        var r = S3ByteRange.Parse("bytes=0-9,20-29", 100);
        Assert.IsType<S3ByteRange.Ignored>(r);
    }

    [Fact]
    public void NonBytesUnit_Ignored()
    {
        var r = S3ByteRange.Parse("items=0-9", 100);
        Assert.IsType<S3ByteRange.Ignored>(r);
    }

    [Fact]
    public void Empty_Ignored()
    {
        var r = S3ByteRange.Parse("", 100);
        Assert.IsType<S3ByteRange.Ignored>(r);
    }

    [Fact]
    public void Malformed_Ignored()
    {
        var r = S3ByteRange.Parse("bytes=abc", 100);
        Assert.IsType<S3ByteRange.Ignored>(r);
    }

    [Fact]
    public void EndBeforeStart_Ignored()
    {
        var r = S3ByteRange.Parse("bytes=50-10", 100);
        Assert.IsType<S3ByteRange.Ignored>(r);
    }

    [Fact]
    public void ZeroSizeObject_Suffix_Unsatisfiable()
    {
        var r = S3ByteRange.Parse("bytes=-5", 0);
        Assert.IsType<S3ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void TryParse_Matches_Normal()
    {
        Assert.True(S3ByteRange.TryParse("bytes=10-25", 100, out var start, out var end));
        Assert.Equal(10, start);
        Assert.Equal(25, end);
    }

    [Fact]
    public void TryParse_Returns_False_On_Non_Normal()
    {
        Assert.False(S3ByteRange.TryParse("bytes=500-", 100, out _, out _));
        Assert.False(S3ByteRange.TryParse("invalid", 100, out _, out _));
    }
}
