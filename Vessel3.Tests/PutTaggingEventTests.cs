using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class PutTaggingEventTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "vessel3-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PutTagging_Applies_Via_LogReplay()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        try
        {
            // Phase 1: open bucket, write a put + a put-tagging via the registry, close.
            string versionId;
            using (var b = new Bucket("test", dir))
            {
                b.Open();
                var put = b.AppendPut("k.txt", new PutRequest(
                    BlobSha: new string('0', 64),
                    Md5: new string('0', 32),
                    Size: 5,
                    ContentType: "text/plain",
                    Metadata: new Dictionary<string, string>()));
                versionId = put.VersionId;
                b.AppendPutTagging("k.txt", versionId,
                    new Dictionary<string, string> { ["env"] = "prod" });
            }

            // Phase 2: reopen — index gets rebuilt from log.
            // Force fresh index by deleting the sqlite db so replay must reapply.
            File.Delete(Path.Combine(dir, "index.db"));
            File.Delete(Path.Combine(dir, "index.db-wal"));
            File.Delete(Path.Combine(dir, "index.db-shm"));

            using var reopened = new Bucket("test", dir);
            reopened.Open();
            var got = reopened.Index.GetVersion("k.txt", versionId);
            var entry = ((Result<PutEntry?>.Success)got).Value!;
            Assert.NotNull(entry.Tags);
            Assert.Equal("prod", entry.Tags!["env"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteTagging_Clears_TagSet()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        try
        {
            using var b = new Bucket("test", dir);
            b.Open();
            var put = b.AppendPut("k.txt", new PutRequest(
                BlobSha: new string('1', 64),
                Md5: new string('1', 32),
                Size: 1,
                ContentType: "text/plain",
                Metadata: new Dictionary<string, string>(),
                Tags: new Dictionary<string, string> { ["a"] = "1" }));

            b.AppendPutTagging("k.txt", put.VersionId, new Dictionary<string, string>());

            var got = b.Index.GetVersion("k.txt", put.VersionId);
            var entry = ((Result<PutEntry?>.Success)got).Value!;
            Assert.Empty(entry.Tags!);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CurrentKind_Distinguishes_Put_And_DeleteMarker()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        try
        {
            using var b = new Bucket("test", dir);
            b.Open();
            b.SetVersioning(VersioningStatus.Enabled);

            Assert.Null(b.Index.GetCurrentKind("missing"));

            b.AppendPut("k.txt", new PutRequest(
                BlobSha: new string('2', 64), Md5: new string('2', 32), Size: 1,
                ContentType: "text/plain", Metadata: new Dictionary<string, string>()));
            Assert.Equal(BucketIndex.KindPut, b.Index.GetCurrentKind("k.txt"));

            b.AppendDelete("k.txt", bypassGovernance: false);
            Assert.Equal(BucketIndex.KindDeleteMarker, b.Index.GetCurrentKind("k.txt"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
