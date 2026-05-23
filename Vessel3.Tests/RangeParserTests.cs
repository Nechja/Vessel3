using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public class RangeParserTests
{
    [Fact]
    public void Normal_ClosedRange()
    {
        var r = RequestHelpers.ParseByteRange("bytes=0-9", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(9, n.End);
    }

    [Fact]
    public void Normal_MidRange()
    {
        var r = RequestHelpers.ParseByteRange("bytes=10-19", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(10, n.Start);
        Assert.Equal(19, n.End);
    }

    [Fact]
    public void OpenEnd_ServesToEnd()
    {
        var r = RequestHelpers.ParseByteRange("bytes=50-", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(50, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void OpenEnd_Zero_FullObject()
    {
        var r = RequestHelpers.ParseByteRange("bytes=0-", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_LastNBytes()
    {
        var r = RequestHelpers.ParseByteRange("bytes=-10", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(90, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_LargerThanSize_ClampsToFullObject()
    {
        var r = RequestHelpers.ParseByteRange("bytes=-9999", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(0, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void Suffix_Zero_Unsatisfiable()
    {
        var r = RequestHelpers.ParseByteRange("bytes=-0", 100);
        Assert.IsType<RequestHelpers.ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void StartAtSize_Unsatisfiable()
    {
        var r = RequestHelpers.ParseByteRange("bytes=100-200", 100);
        Assert.IsType<RequestHelpers.ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void StartBeyondSize_Unsatisfiable()
    {
        var r = RequestHelpers.ParseByteRange("bytes=500-", 100);
        Assert.IsType<RequestHelpers.ByteRange.Unsatisfiable>(r);
    }

    [Fact]
    public void EndPastSize_ClampedToLastByte()
    {
        var r = RequestHelpers.ParseByteRange("bytes=50-9999", 100);
        var n = Assert.IsType<RequestHelpers.ByteRange.Normal>(r);
        Assert.Equal(50, n.Start);
        Assert.Equal(99, n.End);
    }

    [Fact]
    public void MultiRange_Ignored()
    {
        var r = RequestHelpers.ParseByteRange("bytes=0-9,20-29", 100);
        Assert.IsType<RequestHelpers.ByteRange.Ignored>(r);
    }

    [Fact]
    public void NonBytesUnit_Ignored()
    {
        var r = RequestHelpers.ParseByteRange("items=0-9", 100);
        Assert.IsType<RequestHelpers.ByteRange.Ignored>(r);
    }

    [Fact]
    public void Empty_Ignored()
    {
        var r = RequestHelpers.ParseByteRange("", 100);
        Assert.IsType<RequestHelpers.ByteRange.Ignored>(r);
    }

    [Fact]
    public void Malformed_Ignored()
    {
        var r = RequestHelpers.ParseByteRange("bytes=abc", 100);
        Assert.IsType<RequestHelpers.ByteRange.Ignored>(r);
    }

    [Fact]
    public void EndBeforeStart_Ignored()
    {
        var r = RequestHelpers.ParseByteRange("bytes=50-10", 100);
        Assert.IsType<RequestHelpers.ByteRange.Ignored>(r);
    }

    [Fact]
    public void ZeroSizeObject_Suffix_Unsatisfiable()
    {
        var r = RequestHelpers.ParseByteRange("bytes=-5", 0);
        Assert.IsType<RequestHelpers.ByteRange.Unsatisfiable>(r);
    }
}
