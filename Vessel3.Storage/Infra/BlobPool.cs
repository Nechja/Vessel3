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
    Task<Result<Stream>> OpenLocalVolumeOnly(string sha, CancellationToken ct = default) => Open(sha, ct);
    Task<bool> Exists(string sha, CancellationToken ct = default);
    Task<bool> ExistsOnLocalVolume(string sha, CancellationToken ct = default) => Exists(sha, ct);
    Task<Result<bool>> Delete(string sha, CancellationToken ct = default);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}

internal sealed class BlobPool(IVolumeRegistry registry, IBlobLocationCatalog catalog, IBlobMirrorQueue? mirrorQueue = null) : IBlobPool
{
    public BlobPool(BlobPoolOptions options, IFileSync fileSync)
        : this(new VolumeRegistry([new StorageVolume(VesselDefaults.DefaultVolumeId, options.Root, VesselDefaults.DefaultVolumePool, VolumeCapabilities.Ingest)], fileSync), NullBlobLocationCatalog.Instance)
    {
    }

    public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct)
    {
        if (!registry.DefaultIngestVolume.IsWritable)
            return new BucketIsReadOnlyError("Node volume is configured as read-only.");

        var storage = registry.DefaultIngestStorage;
        var writeResult = await storage.WriteStagedBlob(source, declaredSize, intent, ct);

        if (!writeResult.TryGetValue(out var stored, out var err))
            return err;

        catalog.RecordLocation(stored.Sha, storage.VolumeId);

        foreach (var mirror in registry.MirrorTargetStorages)
        {
            if (string.Equals(mirror.VolumeId, storage.VolumeId, StringComparison.Ordinal))
                continue;

            var mirrorVol = registry.GetVolume(mirror.VolumeId);
            if (mirrorVol is { IsOnDemand: true })
            {
                mirrorQueue?.Enqueue(stored.Sha);
                continue;
            }

            if ((await storage.OpenBlob(stored.Sha, ct)).TryGetValue(out var localStream, out _))
            {
                await using (localStream)
                {
                    _ = await mirror.WriteStagedBlob(localStream, stored.Size, ChecksumIntent.None, ct);
                }
            }
        }

        return stored;
    }

    public async Task<Result<Stream>> Open(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            if (registry.TryGetOnlineReader(locatedVolumeId, out var locatedReader)
                && (await locatedReader.OpenBlob(sha, ct)).TryGetValue(out var locatedStream, out _))
            {
                QueueSelfHealIfRemote(locatedVolumeId, sha);
                return locatedStream;
            }

            catalog.RemoveLocation(sha);
        }

        foreach (var reader in registry.ReadPriorityReaders)
        {
            if (string.Equals(reader.VolumeId, locatedVolumeId, StringComparison.Ordinal))
                continue;

            if (!(await reader.OpenBlob(sha, ct)).TryGetValue(out var stream, out _))
                continue;

            catalog.RecordLocation(sha, reader.VolumeId);
            QueueSelfHealIfRemote(reader.VolumeId, sha);
            return stream;
        }

        return new NotFoundError($"blob {sha}");
    }

    private void QueueSelfHealIfRemote(string volumeId, string sha)
    {
        if (mirrorQueue is null || registry.MirrorTargetStorages.Count == 0)
            return;

        if (registry.GetVolume(volumeId) is { IsRemote: true })
            mirrorQueue.Enqueue(sha);
    }

    public async Task<Result<Stream>> OpenLocalVolumeOnly(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null
            && registry.TryGetOnlineLocalStorage(locatedVolumeId, out var locatedStorage))
        {
            if ((await locatedStorage.OpenBlob(sha, ct)).TryGetValue(out var locatedStream, out _))
                return locatedStream;

            catalog.RemoveLocation(sha);
        }

        foreach (var storage in registry.LocalStorages)
        {
            if (string.Equals(storage.VolumeId, locatedVolumeId, StringComparison.Ordinal))
                continue;

            if (!(await storage.OpenBlob(sha, ct)).TryGetValue(out var stream, out _))
                continue;

            catalog.RecordLocation(sha, storage.VolumeId);
            return stream;
        }

        return new NotFoundError($"blob {sha}");
    }

    public async Task<bool> Exists(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            if (registry.TryGetOnlineReader(locatedVolumeId, out var locatedReader)
                && await locatedReader.BlobExists(sha, ct))
            {
                return true;
            }

            catalog.RemoveLocation(sha);
        }

        foreach (var reader in registry.ReadPriorityReaders)
        {
            if (string.Equals(reader.VolumeId, locatedVolumeId, StringComparison.Ordinal))
                continue;

            if (!await reader.BlobExists(sha, ct))
                continue;

            catalog.RecordLocation(sha, reader.VolumeId);
            return true;
        }

        return false;
    }

    public async Task<bool> ExistsOnLocalVolume(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null
            && registry.TryGetOnlineLocalStorage(locatedVolumeId, out var locatedStorage))
        {
            if (await locatedStorage.BlobExists(sha, ct))
                return true;

            catalog.RemoveLocation(sha);
        }

        foreach (var storage in registry.LocalStorages)
        {
            if (string.Equals(storage.VolumeId, locatedVolumeId, StringComparison.Ordinal))
                continue;

            if (!await storage.BlobExists(sha, ct))
                continue;

            catalog.RecordLocation(sha, storage.VolumeId);
            return true;
        }

        return false;
    }

    public async Task<Result<bool>> Delete(string sha, CancellationToken ct = default)
    {
        var deletedAny = false;
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (var storage in registry.WritableStorages)
        {
            if (visited.Add(storage.VolumeId)
                && await storage.DeleteBlob(sha, ct) is Result<bool>.Success { Value: true })
            {
                deletedAny = true;
            }
        }

        foreach (var storage in registry.MirrorTargetStorages)
        {
            if (visited.Add(storage.VolumeId)
                && await storage.DeleteBlob(sha, ct) is Result<bool>.Success { Value: true })
            {
                deletedAny = true;
            }
        }

        catalog.RemoveLocation(sha);
        return deletedAny;
    }

    public IEnumerable<string> EnumerateShards()
    {
        var storages = registry.LocalStorages;
        if (storages is [var single])
            return single.EnumerateShards();

        HashSet<string> shards = new(StringComparer.Ordinal);
        foreach (var storage in storages)
            shards.UnionWith(storage.EnumerateShards());
        return shards;
    }

    public IEnumerable<string> Enumerate(string shard)
    {
        var storages = registry.LocalStorages;
        if (storages is [var single])
            return single.Enumerate(shard);

        HashSet<string> shas = new(StringComparer.Ordinal);
        foreach (var storage in storages)
            shas.UnionWith(storage.Enumerate(shard));
        return shas;
    }

    public DateTime? GetLastWriteUtc(string sha)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null
            && registry.TryGetOnlineLocalStorage(locatedVolumeId, out var locatedStorage))
        {
            if (locatedStorage.GetLastWriteUtc(sha) is { } locatedMtime)
                return locatedMtime;

            catalog.RemoveLocation(sha);
        }

        foreach (var storage in registry.LocalStorages)
        {
            if (string.Equals(storage.VolumeId, locatedVolumeId, StringComparison.Ordinal))
                continue;

            if (storage.GetLastWriteUtc(sha) is { } mtime)
            {
                catalog.RecordLocation(sha, storage.VolumeId);
                return mtime;
            }
        }

        return null;
    }

    public int ReapAbandonedTempFiles(DateTime cutoffUtc)
    {
        var reaped = 0;
        foreach (var storage in registry.LocalStorages)
            reaped += storage.ReapAbandonedTempFiles(cutoffUtc);
        return reaped;
    }
}
