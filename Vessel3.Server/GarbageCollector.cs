using System.Text.Json.Serialization;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record GcReport(int BlobsDeleted, int UploadsReaped);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GcReport))]
[JsonSerializable(typeof(LifecycleReport))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;

internal interface IGarbageCollector
{
    GcReport Run(TimeSpan minBlobAge, TimeSpan minUploadAge);
}

internal sealed class GarbageCollector(IBlobPool blobs, IBucketRegistry registry, IMultipartStore multipart) : IGarbageCollector
{
    private readonly Lock gate = new();

    public GcReport Run(TimeSpan minBlobAge, TimeSpan minUploadAge)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var blobCutoff = now - minBlobAge;
            var uploadCutoff = now - minUploadAge;

            var candidates = blobs.EnumerateAll().ToList();

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sha in registry.AllReferencedBlobs()) referenced.Add(sha);
            foreach (var sha in multipart.EnumerateInFlightPartShas()) referenced.Add(sha);

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
    }
}
