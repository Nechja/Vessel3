using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class CompactionTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());

    public CompactionTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-compact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static PutRequest Req(string body) => new(
        BlobSha: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Md5: Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    private Bucket OpenBucket()
    {
        var b = new Bucket("b", root, sync, durable);
        b.Open();
        return b;
    }

    [Fact]
    public void Compact_empties_log_and_preserves_state()
    {
        using (var b = OpenBucket())
        {
            for (var i = 0; i < 20; i++) b.AppendPut($"k{i}", Req($"body{i}"));
            for (var i = 0; i < 10; i++) b.AppendDelete($"k{i}", false);
            Assert.True(b.LogBytes() > 0);

            var outcome = Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact()).Value;
            Assert.True(outcome.LogBytesBefore > 0);
            Assert.Equal(0, outcome.LogBytesAfter);
        }

        using (var b = OpenBucket())
        {
            var live = b.Index.ListCurrent(prefix: null, startAfter: null);
            Assert.Equal(10, live.Count);
        }
    }

    [Fact]
    public void Wiped_index_rebuilds_from_snapshot()
    {
        using (var b = OpenBucket())
        {
            for (var i = 0; i < 5; i++) b.AppendPut($"k{i}", Req($"body{i}"));
            Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
        }

        File.Delete(Path.Combine(root, "index.db"));

        using (var b = OpenBucket())
        {
            var live = b.Index.ListCurrent(prefix: null, startAfter: null);
            Assert.Equal(5, live.Count);
        }
    }

    [Fact]
    public void Wiped_index_rebuilds_from_snapshot_plus_tail()
    {
        string lateVersion;
        using (var b = OpenBucket())
        {
            for (var i = 0; i < 5; i++) b.AppendPut($"k{i}", Req($"body{i}"));
            Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
            lateVersion = b.AppendPut("late", Req("late")).VersionId;
            b.AppendDelete("k0", false);
        }

        File.Delete(Path.Combine(root, "index.db"));

        using (var b = OpenBucket())
        {
            var live = b.Index.ListCurrent(prefix: null, startAfter: null);
            Assert.Equal(5, live.Count);
            var late = ((Result<PutEntry?>.Success)b.Index.GetCurrentPut("late")).Value!;
            Assert.Equal(lateVersion, late.VersionId);
            Assert.Null(((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k0")).Value);
        }
    }

    [Fact]
    public void Snapshot_without_log_rewrite_replays_clean()
    {
        using (var b = OpenBucket())
        {
            for (var i = 0; i < 5; i++) b.AppendPut($"k{i}", Req($"body{i}"));
            b.Index.SnapshotTo(Path.Combine(root, "snapshot.db"));
            b.AppendPut("after", Req("after"));
        }

        File.Delete(Path.Combine(root, "index.db"));

        using (var b = OpenBucket())
        {
            var live = b.Index.ListCurrent(prefix: null, startAfter: null);
            Assert.Equal(6, live.Count);
        }
    }

    [Fact]
    public void Writes_after_compact_survive_replay()
    {
        using (var b = OpenBucket())
        {
            b.SetVersioning(VersioningStatus.Enabled);
            for (var i = 0; i < 5; i++) b.AppendPut("k", Req($"body{i}"));
            Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
            b.AppendPut("k", Req("post-compact"));
            b.AppendDelete("k", false);
        }

        using (var b = OpenBucket())
        {
            Assert.Equal(VersionKind.DeleteMarker, b.Index.GetCurrentKind("k"));
            var (entries, _) = b.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
            Assert.Equal(7, entries.Count);
        }
    }

    [Fact]
    public void Repeated_compaction_is_stable()
    {
        using var b = OpenBucket();
        b.AppendPut("k", Req("one"));
        Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
        Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
        b.AppendPut("k", Req("two"));
        Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact());
        Assert.Equal(0, b.LogBytes());
        Assert.Equal("two".Length, ((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k")).Value!.Size);
    }

    [Fact]
    public void Delete_everything_still_compacts_to_empty()
    {
        using (var b = OpenBucket())
        {
            for (var i = 0; i < 5; i++) b.AppendPut($"k{i}", Req($"body{i}"));
            for (var i = 0; i < 5; i++) b.AppendDelete($"k{i}", false);

            var outcome = Assert.IsType<Result<CompactionOutcome>.Success>(b.Compact()).Value;
            Assert.Equal(0, outcome.LogBytesAfter);
            Assert.True(b.Index.IsEmpty());
        }

        using (var b = OpenBucket())
        {
            b.AppendPut("new", Req("new"));
        }

        File.Delete(Path.Combine(root, "index.db"));

        using (var b = OpenBucket())
        {
            var live = b.Index.ListCurrent(prefix: null, startAfter: null);
            var entry = Assert.Single(live);
            Assert.Equal("new", entry.Key);
        }
    }

    [Fact]
    public void VersionLog_compact_keeps_tail_events()
    {
        var logPath = Path.Combine(root, "log");
        using (var log = new VersionLog(logPath, sync))
        {
            log.Open(1);
            for (var i = 0; i < 6; i++)
                log.Append(new PutEvent(0, DateTimeOffset.UtcNow, $"k{i}", $"v{i}", "aa", "bb", 1, "text/plain", new Dictionary<string, string>()));
            log.Compact(throughSeq: 4);
            log.Append(new PutEvent(0, DateTimeOffset.UtcNow, "k9", "v9", "aa", "bb", 1, "text/plain", new Dictionary<string, string>()));
        }

        using var reopened = new VersionLog(logPath, sync);
        var seqs = reopened.Replay().Select(e => e.Seq).ToList();
        Assert.Equal([5L, 6L, 7L], seqs);
    }
}
