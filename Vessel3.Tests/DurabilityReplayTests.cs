using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class DurabilityReplayTests : IDisposable
{
    private readonly string root;

    public DurabilityReplayTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-replay-{Guid.NewGuid():N}");
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

    [Fact]
    public void Restart_Replays_Versions_From_Log()
    {
        string v1, v2;
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.SetVersioning(VersioningStatus.Enabled);
            v1 = b.AppendPut("k", Req("one")).VersionId;
            v2 = b.AppendPut("k", Req("two")).VersionId;
        }

        using (var b = new Bucket("b", root))
        {
            b.Open();
            var current = ((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k")).Value!;
            Assert.Equal(v2, current.VersionId);
            var old = ((Result<PutEntry?>.Success)b.Index.GetVersion("k", v1)).Value!;
            Assert.Equal("one".Length, old.Size);
        }
    }

    [Fact]
    public void Replay_Recovers_When_Index_Is_Behind_Log()
    {
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("orphan"u8.ToArray()));
        string md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("orphan"u8.ToArray()));

        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("k", Req("a"));
        }

        var logPath = Path.Combine(root, "log");
        using (var log = new VersionLog(logPath))
        {
            log.Open(1_000_000);
            log.Append(new PutEvent(
                0, DateTimeOffset.UtcNow, "k2", "abc",
                sha, md5, 6, "text/plain", new Dictionary<string, string>()));
        }

        using (var b = new Bucket("b", root))
        {
            b.Open();
            var k2 = ((Result<PutEntry?>.Success)b.Index.GetVersion("k2", "abc")).Value;
            Assert.NotNull(k2);
            Assert.Equal(6, k2!.Size);
        }
    }
}
