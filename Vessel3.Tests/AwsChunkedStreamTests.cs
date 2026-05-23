using System.Globalization;
using System.Text;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class AwsChunkedStreamTests
{
    private static byte[] BuildChunks(params byte[][] chunks)
    {
        var ms = new MemoryStream();
        foreach (var chunk in chunks)
        {
            var header = Encoding.ASCII.GetBytes(chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            ms.Write(header);
            ms.Write(chunk);
            ms.Write("\r\n"u8);
        }
        ms.Write("0\r\n\r\n"u8);
        ms.Position = 0;
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadAll(Stream s)
    {
        var dst = new MemoryStream();
        await s.CopyToAsync(dst);
        return dst.ToArray();
    }

    [Fact]
    public async Task SingleChunk_RoundTrips()
    {
        var payload = "Hello"u8.ToArray();
        var input = new MemoryStream(BuildChunks(payload));
        using var s = new AwsChunkedStream(input);

        var got = await ReadAll(s);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task MultipleChunks_Concatenate()
    {
        var a = "Hello, "u8.ToArray();
        var b = "world!"u8.ToArray();
        var input = new MemoryStream(BuildChunks(a, b));
        using var s = new AwsChunkedStream(input);

        var got = await ReadAll(s);
        Assert.Equal("Hello, world!"u8.ToArray(), got);
    }

    [Fact]
    public async Task EmptyBody_ReadsNothing()
    {
        var input = new MemoryStream(BuildChunks());
        using var s = new AwsChunkedStream(input);

        var got = await ReadAll(s);
        Assert.Empty(got);
    }

    [Fact]
    public async Task BadChunkTerminator_Throws()
    {
        // Missing \r\n after chunk data.
        var bad = "5\r\nhello0\r\n\r\n"u8.ToArray();
        var input = new MemoryStream(bad);
        using var s = new AwsChunkedStream(input);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ReadAll(s));
    }
}
