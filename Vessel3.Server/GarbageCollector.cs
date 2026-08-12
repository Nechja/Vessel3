using System.Text.Json.Serialization;
using Vessel3.Server.Lifecycle;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record GcReport(int BlobsDeleted, int UploadsReaped, bool TimedOut = false);

internal sealed record GcOptions(TimeSpan MaxWait);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GcReport))]
[JsonSerializable(typeof(LifecycleReport))]
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

        var candidates = blobs.EnumerateAll().ToList();

        var referenced = ScanInFlightThenCommittedReferences();

        var deleted = 0;
        foreach (var sha in candidates)
        {
            if (referenced.Contains(sha)) continue;
            var mtime = blobs.GetLastWriteUtc(sha);
            if (mtime is null || mtime > blobCutoff) continue;
            if (blobs.Delete(sha) is Result<bool>.Success { Value: true }) deleted++;
        }

        var reaped = multipart.ReapAbandonedUploads(uploadCutoff);
        return new GcReport(deleted, reaped);
    }

    private HashSet<string> ScanInFlightThenCommittedReferences()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sha in multipart.EnumerateInFlightPartShas()) referenced.Add(sha);
        foreach (var sha in registry.AllReferencedBlobs()) referenced.Add(sha);
        return referenced;
    }
}
