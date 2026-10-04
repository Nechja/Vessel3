using Xunit;

namespace Vessel3.Tests;

public class KeyRangeTests
{
    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("a/", "a0")]
    [InlineData("aü", "aý")]
    [InlineData("a😀", "a😁")]
    [InlineData("a퟿", "a")]
    [InlineData("a\U0010FFFF", "b")]
    [InlineData("a\U0010FFFF\U0010FFFF", "b")]
    public void Successor_PrefixedKeys_ReturnsNextKey(string prefix, string expected) =>
        Assert.Equal(expected, KeyRange.Successor(prefix));

    [Theory]
    [InlineData("")]
    [InlineData("\U0010FFFF")]
    [InlineData("\U0010FFFF\U0010FFFF")]
    public void Successor_NothingFollows_ReturnsNull(string prefix) =>
        Assert.Null(KeyRange.Successor(prefix));

    [Theory]
    [InlineData("abc")]
    [InlineData("a/")]
    [InlineData("aü")]
    [InlineData("a😀")]
    [InlineData("a퟿")]
    [InlineData("a\U0010FFFF")]
    public void Successor_Utf8Encoding_OrdersCorrectly(string prefix)
    {
        var hi = KeyRange.Successor(prefix)!;
        static int Cmp(string a, string b) => System.Text.Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(b));
        Assert.True(Cmp(prefix, hi) < 0);
        foreach (var tail in new[] { "", "\0", "z", "\U0010FFFF", "\uFFFD", "😀" })
            Assert.True(Cmp(prefix + tail, hi) < 0, $"{prefix + tail} should sort before {hi}");
    }
}
