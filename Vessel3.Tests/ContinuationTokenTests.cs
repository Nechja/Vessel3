using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class ContinuationTokenTests
{
    [Fact]
    public void EncodeAndDecode_RoundtripsSuccessfully()
    {
        var original = "photos/2026/summer.jpg";
        var token = ContinuationToken.Encode(original);
        Assert.NotNull(token);
        Assert.True(ContinuationToken.TryDecode(token, out var decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryDecode_InvalidBase64_ReturnsFalse()
    {
        Assert.False(ContinuationToken.TryDecode("!!!invalid-base64???", out var key));
        Assert.Null(key);
    }

    [Fact]
    public void TryDecode_EmptyOrNull_ReturnsFalse()
    {
        Assert.False(ContinuationToken.TryDecode("", out var key1));
        Assert.Null(key1);
    }

    [Fact]
    public void TryDecode_UnicodeKey_RoundtripsSuccessfully()
    {
        var original = "documents/café/résumé_🚀.pdf";
        var token = ContinuationToken.Encode(original);
        Assert.True(ContinuationToken.TryDecode(token, out var decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryDecode_LongKey_RoundtripsSuccessfully()
    {
        var original = new string('x', 800) + "/tail";
        var token = ContinuationToken.Encode(original);
        Assert.True(ContinuationToken.TryDecode(token, out var decoded));
        Assert.Equal(original, decoded);
    }
}
