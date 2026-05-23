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
        var bad = "5\r\nhello0\r\n\r\n"u8.ToArray();
        var input = new MemoryStream(bad);
        using var s = new AwsChunkedStream(input);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ReadAll(s));
    }

    private static byte[] BuildChunksWithTrailers(byte[] data, params (string Name, string Value)[] trailers)
    {
        var ms = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(data.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
        ms.Write(header);
        ms.Write(data);
        ms.Write("\r\n"u8);
        ms.Write("0\r\n"u8);
        foreach (var (n, v) in trailers)
            ms.Write(Encoding.ASCII.GetBytes($"{n}:{v}\r\n"));
        ms.Write("\r\n"u8);
        ms.Position = 0;
        return ms.ToArray();
    }

    [Fact]
    public async Task UnsignedPayloadTrailer_RoundTrips_AndExposesTrailers()
    {
        var payload = "boto3 payload"u8.ToArray();
        var raw = BuildChunksWithTrailers(payload, ("x-amz-checksum-crc32", "q+nC1A=="));
        var input = new MemoryStream(raw);
        using var s = new AwsChunkedStream(input);

        var got = await ReadAll(s);
        Assert.Equal(payload, got);
        Assert.Equal("q+nC1A==", s.Trailers["x-amz-checksum-crc32"]);
    }

    [Fact]
    public async Task TrailerSignatureLine_Ignored()
    {
        var payload = "x"u8.ToArray();
        var raw = BuildChunksWithTrailers(payload,
            ("x-amz-checksum-crc32", "AAAAAA=="),
            ("x-amz-trailer-signature", "deadbeef"));
        var input = new MemoryStream(raw);
        using var s = new AwsChunkedStream(input);

        await ReadAll(s);
        Assert.True(s.Trailers.ContainsKey("x-amz-checksum-crc32"));
        Assert.False(s.Trailers.ContainsKey("x-amz-trailer-signature"));
    }
}
