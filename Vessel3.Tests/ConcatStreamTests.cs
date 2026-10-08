using Xunit;

namespace Vessel3.Tests;

public class ConcatStreamTests
{
    private sealed class FakeBlobs : IBlobPool
    {
        private readonly Dictionary<string, byte[]> map;
        public FakeBlobs(Dictionary<string, byte[]> map) => this.map = map;

        public Task<Result<Stream>> Open(string sha, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(sha, out var bytes)
                ? (Result<Stream>)new MemoryStream(bytes, writable: false)
                : new NotFoundError($"blob {sha}"));

        public Task<Result<StoredBlob>> Write(Stream s, long? sz, ChecksumIntent intent, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> Exists(string sha, CancellationToken ct = default) => Task.FromResult(map.ContainsKey(sha));
        public Task<Result<bool>> Delete(string sha, CancellationToken ct = default) => Task.FromResult<Result<bool>>(map.Remove(sha));
        public IEnumerable<string> EnumerateShards() => map.Keys.Select(k => k[..2]).Distinct(StringComparer.Ordinal);
        public IEnumerable<string> Enumerate(string shard) => map.Keys.Where(k => k.StartsWith(shard, StringComparison.Ordinal));
        public DateTime? GetLastWriteUtc(string sha) => map.ContainsKey(sha) ? DateTime.UtcNow : null;
        public int ReapAbandonedTempFiles(DateTime cutoffUtc) => 0;
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
        await s.CopyToAsync(sink, TestContext.Current.CancellationToken);
        Assert.Equal("hello world!"u8.ToArray(), sink.ToArray());
    }

    [Fact]
    public async Task Seek_AcrossParts()
    {
        var (parts, blobs) = Setup("0123"u8.ToArray(), "4567"u8.ToArray(), "89"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        s.Seek(5, SeekOrigin.Begin);
        Assert.Equal(5, s.Position);

        var buf = new byte[4];
        await s.ReadExactlyAsync(buf, TestContext.Current.CancellationToken);
        Assert.Equal("5678"u8.ToArray(), buf);
    }

    [Fact]
    public void Read_SynchronousRead_ThrowsNotSupportedException()
    {
        var (parts, blobs) = Setup("0123"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        Assert.Throws<NotSupportedException>(() => s.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Seek_Beyond_Throws()
    {
        var (parts, blobs) = Setup("ab"u8.ToArray());
        using var s = new ConcatStream(parts, blobs);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Seek(99, SeekOrigin.Begin));
    }
}
