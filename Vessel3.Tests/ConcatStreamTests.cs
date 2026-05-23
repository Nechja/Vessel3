using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class ConcatStreamTests
{
    private sealed class FakeBlobs : IBlobPool
    {
        private readonly Dictionary<string, byte[]> map;
        public FakeBlobs(Dictionary<string, byte[]> map) => this.map = map;

        public Result<Stream> Open(string sha) =>
            map.TryGetValue(sha, out var bytes)
                ? (Result<Stream>)new MemoryStream(bytes, writable: false)
                : new NotFoundError($"blob {sha}");

        public Task<Result<StoredBlob>> Write(Stream s, long? sz, CancellationToken ct) => throw new NotImplementedException();
        public bool Exists(string sha) => map.ContainsKey(sha);
        public Result<bool> Delete(string sha) => map.Remove(sha);
        public IEnumerable<string> EnumerateAll() => map.Keys;
        public DateTime? GetLastWriteUtc(string sha) => map.ContainsKey(sha) ? DateTime.UtcNow : null;
    }

    private static (List<MultipartPart>, FakeBlobs) Setup(params byte[][] payloads)
    {
        var blobs = new Dictionary<string, byte[]>();
        var parts = new List<MultipartPart>();
        for (var i = 0; i < payloads.Length; i++)
        {
            var sha = $"sha{i:D2}";
            blobs[sha] = payloads[i];
            parts.Add(new MultipartPart(i + 1, sha, $"md5_{i}", payloads[i].Length));
        }
        return (parts, new FakeBlobs(blobs));
    }

    [Fact]
    public void Length_SumsPartSizes()
    {
        var (parts, blobs) = Setup("aaa"u8.ToArray(), "bbbb"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        Assert.Equal(7, s.Length);
    }

    [Fact]
    public async Task Read_StreamsAllParts()
    {
        var (parts, blobs) = Setup("hello "u8.ToArray(), "world"u8.ToArray(), "!"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        var sink = new MemoryStream();
        await s.CopyToAsync(sink);
        Assert.Equal("hello world!"u8.ToArray(), sink.ToArray());
    }

    [Fact]
    public void Seek_AcrossParts()
    {
        // Seek lands inside part 1 ("4567"); reading must produce the right
        // bytes across the boundary into part 2 ("89"). Stream may return
        // short reads at the boundary, so ReadExactly to fill the buffer.
        var (parts, blobs) = Setup("0123"u8.ToArray(), "4567"u8.ToArray(), "89"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        s.Seek(5, SeekOrigin.Begin);
        Assert.Equal(5, s.Position);

        var buf = new byte[4];
        s.ReadExactly(buf);
        Assert.Equal("5678"u8.ToArray(), buf);
    }

    [Fact]
    public void Seek_Beyond_Throws()
    {
        var (parts, blobs) = Setup("ab"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Seek(99, SeekOrigin.Begin));
    }
}
