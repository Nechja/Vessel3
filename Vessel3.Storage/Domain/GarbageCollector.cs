using System.Text.Json.Serialization;

namespace Vessel3.Storage;

internal sealed record GcReport(int BlobsDeleted, int UploadsReaped, bool TimedOut = false, int TempBlobsReaped = 0);

internal sealed record GcOptions(TimeSpan MaxWait, string ScratchRoot);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GcReport))]
[JsonSerializable(typeof(LifecycleReport))]
[JsonSerializable(typeof(CompactionReport))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;

internal interface IGarbageCollector
{
    Task<GcReport> Run(TimeSpan minBlobAge, TimeSpan minUploadAge);
}

internal sealed class GarbageCollector(IBlobPool blobs, IBucketRegistry registry, IMultipartStore multipart, IGcGate gate, GcOptions options) : IGarbageCollector
{
    public async Task<GcReport> Run(TimeSpan minBlobAge, TimeSpan minUploadAge)
    {
        using var lease = await gate.Collecting(options.MaxWait);
        if (lease is null) return new GcReport(0, 0, TimedOut: true);

        var now = DateTime.UtcNow;
        var blobCutoff = now - minBlobAge;
        var uploadCutoff = now - minUploadAge;

        var scratch = Path.Combine(options.ScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            SpillInFlightThenCommittedReferences(scratch);

            var deleted = 0;
            foreach (var shard in blobs.EnumerateShards())
            {
                var referenced = LoadShardReferences(Path.Combine(scratch, shard));
                foreach (var sha in blobs.Enumerate(shard).ToList())
                {
                    if (referenced.Contains(sha)) continue;
                    var mtime = blobs.GetLastWriteUtc(sha);
                    if (mtime is null || mtime > blobCutoff) continue;
                    if (blobs.Delete(sha) is Result<bool>.Success { Value: true }) deleted++;
                }
            }

            var reaped = multipart.ReapAbandonedUploads(uploadCutoff);
            var tempBlobsReaped = blobs.ReapAbandonedTempFiles(blobCutoff);
            return new GcReport(deleted, reaped, TimedOut: false, TempBlobsReaped: tempBlobsReaped);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort scratch cleanup
            }
        }
    }

    private void SpillInFlightThenCommittedReferences(string scratch)
    {
        var writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        try
        {
            foreach (var sha in multipart.EnumerateInFlightPartShas()) Spill(writers, scratch, sha);
            foreach (var sha in registry.AllReferencedBlobs()) Spill(writers, scratch, sha);
        }
        finally
        {
            foreach (var w in writers.Values) w.Dispose();
        }
    }

    private static void Spill(Dictionary<string, StreamWriter> writers, string scratch, string sha)
    {
        if (sha.Length < 2) return;
        var shard = sha[..2];
        if (!writers.TryGetValue(shard, out var writer))
            writers[shard] = writer = new StreamWriter(Path.Combine(scratch, shard));
        writer.WriteLine(sha);
    }

    private static HashSet<string> LoadShardReferences(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (var line in File.ReadLines(path))
            if (line.Length > 0) set.Add(line);
        return set;
    }
}
