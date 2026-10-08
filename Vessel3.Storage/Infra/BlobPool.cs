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
    Task<Result<Stream>> Open(string sha, CancellationToken ct = default);
    Task<bool> Exists(string sha, CancellationToken ct = default);
    Task<Result<bool>> Delete(string sha, CancellationToken ct = default);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}

internal sealed class BlobPool(IVolumeRegistry registry, IBlobLocationCatalog catalog) : IBlobPool
{
    public BlobPool(BlobPoolOptions options, IFileSync fileSync)
        : this(new VolumeRegistry([new StorageVolume("default", options.Root, "default", VolumeCapabilities.Ingest)], fileSync), NullBlobLocationCatalog.Instance)
    {
    }

    public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct)
    {
        var targetVolume = registry.DefaultIngestVolume;
        var storage = registry.GetStorage(targetVolume.Id);
        var writeResult = await storage.WriteStagedBlob(source, declaredSize, intent, ct);

        if (!writeResult.TryGetValue(out var stored, out var err))
            return err;

        catalog.RecordLocation(stored.Sha, targetVolume.Id);
        return stored;
    }

    public async Task<Result<Stream>> Open(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            var storage = registry.GetStorage(locatedVolumeId);
            var openResult = await storage.OpenBlob(sha, ct);
            if (openResult.TryGetValue(out var s, out _)) return s;
            catalog.RemoveLocation(sha);
        }

        foreach (var vol in registry.ReadPriorityVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            var openResult = await storage.OpenBlob(sha, ct);
            if (!openResult.TryGetValue(out var s, out _)) continue;

            catalog.RecordLocation(sha, vol.Id);
            return s;
        }

        return new NotFoundError($"blob {sha}");
    }

    public async Task<bool> Exists(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            var storage = registry.GetStorage(locatedVolumeId);
            if (await storage.BlobExists(sha, ct))
                return true;
            catalog.RemoveLocation(sha);
        }

        foreach (var vol in registry.ReadPriorityVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            if (!await storage.BlobExists(sha, ct)) continue;

            catalog.RecordLocation(sha, vol.Id);
            return true;
        }

        return false;
    }

    public async Task<Result<bool>> Delete(string sha, CancellationToken ct = default)
    {
        var deletedAny = false;
        foreach (var vol in registry.WritableVolumes)
        {
            var storage = registry.GetStorage(vol.Id);
            if (await storage.DeleteBlob(sha, ct) is Result<bool>.Success { Value: true })
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
                shas.Add(sha);
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
