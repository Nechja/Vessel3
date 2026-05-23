using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class PowerLossTests : IDisposable
{
    private readonly string root;

    public PowerLossTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-powerloss-{Guid.NewGuid():N}");
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

    private static void TruncateLog(string root, long bytes)
    {
        var logPath = Path.Combine(root, "log");
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(fs.Length - bytes);
    }

    private static long LogSize(string root) =>
        new FileInfo(Path.Combine(root, "log")).Length;

    private static void WipeIndex(string root)
    {
        foreach (var f in Directory.GetFiles(root, "index*"))
            File.Delete(f);
    }

    [Fact]
    public void Replay_rebuilds_index_when_only_log_survives()
    {
        string v1, v2;
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.SetVersioning(VersioningStatus.Enabled);
            v1 = b.AppendPut("k", Req("one")).VersionId;
            v2 = b.AppendPut("k", Req("two")).VersionId;
        }

        WipeIndex(root);

        using var restored = new Bucket("b", root);
        restored.Open();
        var current = ((Result<PutEntry?>.Success)restored.Index.GetCurrentPut("k")).Value!;
        Assert.Equal(v2, current.VersionId);
        Assert.NotNull(((Result<PutEntry?>.Success)restored.Index.GetVersion("k", v1)).Value);
    }

    [Fact]
    public void Partial_trailing_event_is_dropped_on_recovery()
    {
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.SetVersioning(VersioningStatus.Enabled);
            b.AppendPut("k", Req("complete"));
        }

        var logPath = Path.Combine(root, "log");
        using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            fs.Write("{\"kind\":\"Put\",\"Seq\":99,\"Key\":\"never-flushed"u8);
        }
        var corruptedSize = LogSize(root);

        WipeIndex(root);

        using (var b = new Bucket("b", root))
        {
            b.Open();
            Assert.NotNull(((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k")).Value);
            Assert.False(b.Index.GetCurrentPut("never-flushed") is Result<PutEntry?>.Success { Value: not null });
        }

        Assert.True(LogSize(root) < corruptedSize);

        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("k", Req("post-recovery"));
            var (entries, _) = b.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
            Assert.Equal(2, entries.Count(e => e.Key == "k"));
        }
    }

    [Fact]
    public void Lone_partial_event_yields_empty_state()
    {
        var bucketDir = root;
        Directory.CreateDirectory(bucketDir);
        var logPath = Path.Combine(bucketDir, "log");
        using (var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write))
        {
            fs.Write("{\"kind\":\"Put\",\"Seq\":1,\"Key\":\"k\",\"VersionId\""u8);
        }

        using var b = new Bucket("b", root);
        b.Open();
        Assert.True(b.Index.IsEmpty());
        Assert.Equal(0, LogSize(root));
    }
}
