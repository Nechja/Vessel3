#pragma warning disable CA5350

namespace Vessel3.Storage;

internal sealed record StoredBlob(string Sha, string Md5, string? Crc32, string? Crc32C, string? Sha1, long Size);
internal sealed record BlobPoolOptions(string Root);

internal readonly record struct ChecksumIntent(bool Crc32, bool Crc32C, bool Sha1)
{
    public static ChecksumIntent All { get; } = new(true, true, true);
    public static ChecksumIntent None { get; } = new(false, false, false);
}

internal interface IBlobPool
{
    Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct);
    Result<Stream> Open(string sha);
    bool Exists(string sha);
    Result<bool> Delete(string sha);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}

internal sealed class BlobPool(IVolumeRegistry registry, IBlobLocationCatalog catalog) : IBlobPool
{
    public BlobPool(BlobPoolOptions options, IFileSync fileSync)
        : this(new VolumeRegistry([new StorageVolume("default", options.Root, "default", VolumeCapabilities.Ingest)], fileSync), new MemoryBlobLocationCatalog())
    {
    }

    public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct)
    {
        var targetVolume = registry.DefaultIngestVolume;
        var storage = registry.GetStorage(targetVolume.Id);
        var writeResult = await storage.WriteStagedBlobAsync(source, declaredSize, intent, ct);

        if (!writeResult.TryGetValue(out var stored, out var err))
            return err;

        catalog.RecordLocation(stored.Sha, targetVolume.Id);
        return stored;
    }

    public Result<Stream> Open(string sha)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            var storage = registry.GetStorage(locatedVolumeId);
            var openResult = storage.OpenBlobAsync(sha).GetAwaiter().GetResult();
            if (openResult.TryGetValue(out var s, out _)) return s;
            catalog.RemoveLocation(sha);
        }

        foreach (var vol in registry.ReadPriorityVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            var openResult = storage.OpenBlobAsync(sha).GetAwaiter().GetResult();
            if (!openResult.TryGetValue(out var s, out _)) continue;

            catalog.RecordLocation(sha, vol.Id);
            return s;
        }

        return new NotFoundError($"blob {sha}");
    }

    public bool Exists(string sha)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            var storage = registry.GetStorage(locatedVolumeId);
            if (storage.BlobExistsAsync(sha).GetAwaiter().GetResult())
                return true;
            catalog.RemoveLocation(sha);
        }

        foreach (var vol in registry.ReadPriorityVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            if (!storage.BlobExistsAsync(sha).GetAwaiter().GetResult()) continue;

            catalog.RecordLocation(sha, vol.Id);
            return true;
        }

        return false;
    }

    public Result<bool> Delete(string sha)
    {
        var deletedAny = false;
        foreach (var vol in registry.Volumes)
        {
            var storage = registry.GetStorage(vol.Id);
            if (storage.DeleteBlobAsync(sha).GetAwaiter().GetResult() is Result<bool>.Success { Value: true })
                deletedAny = true;
        }

        catalog.RemoveLocation(sha);
        return deletedAny;
    }

    public IEnumerable<string> EnumerateShards()
    {
        HashSet<string> shards = new(StringComparer.Ordinal);
        foreach (var vol in registry.Volumes)
        {
            var storage = registry.GetStorage(vol.Id);
            foreach (var shard in storage.EnumerateShards())
                shards.Add(shard);
        }
        return shards;
    }

    public IEnumerable<string> Enumerate(string shard)
    {
        HashSet<string> shas = new(StringComparer.Ordinal);
        foreach (var vol in registry.Volumes)
        {
            var storage = registry.GetStorage(vol.Id);
            foreach (var sha in storage.Enumerate(shard))
            {
                catalog.RecordLocation(sha, vol.Id);
                shas.Add(sha);
            }
        }
        return shas;
    }

    public DateTime? GetLastWriteUtc(string sha)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            var storage = registry.GetStorage(locatedVolumeId);
            var mtime = storage.GetLastWriteUtc(sha);
            if (mtime is not null) return mtime;
        }

        foreach (var vol in registry.ReadPriorityVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            var mtime = storage.GetLastWriteUtc(sha);
            if (mtime is not null) return mtime;
        }

        return null;
    }

    public int ReapAbandonedTempFiles(DateTime cutoffUtc)
    {
        var reaped = 0;
        foreach (var vol in registry.Volumes)
        {
            var storage = registry.GetStorage(vol.Id);
            reaped += storage.ReapAbandonedTempFiles(cutoffUtc);
        }
        return reaped;
    }
}
