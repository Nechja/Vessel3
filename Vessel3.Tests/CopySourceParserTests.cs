using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public class CopySourceParserTests
{
    [Theory]
    [InlineData("bucket/key.txt", "bucket", "key.txt")]
    [InlineData("/bucket/key.txt", "bucket", "key.txt")]
    [InlineData("bucket%2Fkey.txt", "bucket", "key.txt")]
    [InlineData("%2Fbucket%2Fkey.txt", "bucket", "key.txt")]
    [InlineData("bucket/a/b/c.txt", "bucket", "a/b/c.txt")]
    [InlineData("bucket%2Fa%2Fb%2Fc.txt", "bucket", "a/b/c.txt")]
    [InlineData("bucket%2Fa%20b.txt", "bucket", "a b.txt")]
    [InlineData("bucket/key.txt?versionId=abc", "bucket", "key.txt")]
    public void Parses(string raw, string expectBucket, string expectKey)
    {
        Assert.True(RequestHelpers.TryParseCopySource(raw, out var bucket, out var key));
        Assert.Equal(expectBucket, bucket);
        Assert.Equal(expectKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bucket")]
    [InlineData("bucket/")]
    [InlineData("/key.txt")]
    [InlineData("%2Fkey.txt")]
    public void Rejects(string raw)
    {
        Assert.False(RequestHelpers.TryParseCopySource(raw, out _, out _));
    }
}
