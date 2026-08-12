using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class LogFormatTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());

    public LogFormatTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-logfmt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(root, "log");

    private static PutRequest Req(string body) => new(
        BlobSha: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Md5: Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    private void WipeIndex()
    {
        foreach (var f in Directory.GetFiles(root, "index.db*")) File.Delete(f);
    }

    private void DropLastRecord()
    {
        var bytes = File.ReadAllBytes(LogPath);
        var last = Array.LastIndexOf(bytes, (byte)'\n');
        Assert.True(last >= 0);
        var previous = last == 0 ? -1 : Array.LastIndexOf(bytes, (byte)'\n', last - 1);
        File.WriteAllBytes(LogPath, bytes[..(previous + 1)]);
    }

    [Fact]
    public void Overwrite_Survives_A_Crash_Before_The_Final_Record()
    {
        using (var b = new Bucket("b", root, sync, durable))
        {
            b.Open();
            b.AppendPut("k", Req("first"));
            b.AppendPut("k", Req("second"));
        }

        DropLastRecord();
        WipeIndex();

        using (var b = new Bucket("b", root, sync, durable))
        {
            b.Open();
            var current = ((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k")).Value;
            Assert.NotNull(current);
        }
    }

    [Fact]
    public void Corrupted_Interior_Record_Is_Rejected_Rather_Than_Silently_Applied()
    {
        using (var b = new Bucket("b", root, sync, durable))
        {
            b.Open();
            b.AppendPut("a", Req("a"));
            b.AppendPut("bb", Req("bb"));
            b.AppendPut("ccc", Req("ccc"));
        }

        var text = File.ReadAllText(LogPath);
        Assert.Contains("\"Size\":1,", text, StringComparison.Ordinal);
        File.WriteAllText(LogPath, text.Replace("\"Size\":1,", "\"Size\":9,", StringComparison.Ordinal));
        WipeIndex();

        using var reopened = new Bucket("b", root, sync, durable);
        Assert.Throws<InvalidDataException>(reopened.Open);
    }

    [Fact]
    public void Torn_Trailing_Record_Is_Truncated_And_Earlier_Records_Survive()
    {
        using (var b = new Bucket("b", root, sync, durable))
        {
            b.Open();
            b.AppendPut("k", Req("first"));
            b.AppendPut("j", Req("second"));
        }

        var bytes = File.ReadAllBytes(LogPath);
        File.WriteAllBytes(LogPath, bytes[..(bytes.Length - 12)]);
        WipeIndex();

        using (var b = new Bucket("b", root, sync, durable))
        {
            b.Open();
            var kept = ((Result<PutEntry?>.Success)b.Index.GetCurrentPut("k")).Value;
            Assert.NotNull(kept);
        }
    }
}
