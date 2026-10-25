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
        var storage = registry.DefaultIngestStorage;
        var writeResult = await storage.WriteStagedBlob(source, declaredSize, intent, ct);

        if (!writeResult.TryGetValue(out var stored, out var err))
            return err;

        catalog.RecordLocation(stored.Sha, storage.VolumeId);
        return stored;
    }

    public async Task<Result<Stream>> Open(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            if (registry.TryGetReadableReader(locatedVolumeId, out var locatedReader)
                && (await locatedReader.OpenBlob(sha, ct)).TryGetValue(out var locatedStream, out _))
            {
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
            return stream;
        }

        return new NotFoundError($"blob {sha}");
    }

    public async Task<bool> Exists(string sha, CancellationToken ct = default)
    {
        var locatedVolumeId = catalog.LocateBlob(sha);
        if (locatedVolumeId is not null)
        {
            if (registry.TryGetReadableReader(locatedVolumeId, out var locatedReader)
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

    public async Task<Result<bool>> Delete(string sha, CancellationToken ct = default)
    {
        var deletedAny = false;
        foreach (var storage in registry.WritableStorages)
        {
            if (await storage.DeleteBlob(sha, ct) is Result<bool>.Success { Value: true })
                deletedAny = true;
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
        if (catalog.LocateBlob(sha) is { } locatedVolumeId
            && registry.TryGetReadableStorage(locatedVolumeId, out var locatedStorage)
            && locatedStorage.GetLastWriteUtc(sha) is { } locatedMtime)
        {
            return locatedMtime;
        }

        foreach (var storage in registry.LocalStorages)
        {
            if (storage.GetLastWriteUtc(sha) is { } mtime)
                return mtime;
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
