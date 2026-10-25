using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal sealed class VolumeRegistry(
    IReadOnlyList<StorageVolume> volumes,
    IFileSync fileSync,
    Func<StorageVolume, IVolumeStorage>? storageFactory = null) : IVolumeRegistry
{
    private readonly Func<StorageVolume, IVolumeStorage> factory =
        storageFactory ?? (v => new LocalDiskVolumeStorage(v, fileSync));
    private readonly Lock gate = new();
    private volatile RegistrySnapshot current = BuildInitialSnapshot(
        volumes,
        storageFactory ?? (v => new LocalDiskVolumeStorage(v, fileSync)));

    public IReadOnlyList<StorageVolume> Volumes => current.Volumes;
    public IReadOnlyList<StorageVolume> WritableVolumes => current.WritableVolumes;
    public IReadOnlyList<StorageVolume> ReadPriorityVolumes => current.ReadPriorityVolumes;
    public IReadOnlyList<IVolumeStorage> WritableStorages => current.WritableStorages;
    public IReadOnlyList<IVolumeStorage> ReadPriorityStorages => current.ReadPriorityStorages;
    public StorageVolume DefaultIngestVolume => current.DefaultIngestVolume;
    public IVolumeStorage DefaultIngestStorage => current.DefaultIngestStorage;

    public StorageVolume? GetVolume(string id) =>
        current.VolumesById.GetValueOrDefault(id);

    public IReadOnlyList<StorageVolume> GetPool(string pool) =>
        current.Pools.GetValueOrDefault(pool) ?? [];

    public IVolumeStorage GetStorage(string volumeId) =>
        current.Storages.TryGetValue(volumeId, out var storage)
            ? storage
            : throw new KeyNotFoundException($"Storage volume '{volumeId}' not found.");

    public bool TryGetReadableStorage(string volumeId, [NotNullWhen(true)] out IVolumeStorage? storage) =>
        current.ReadableStorages.TryGetValue(volumeId, out storage);

    public void UpsertVolume(StorageVolume volume, IVolumeStorage? storage = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        lock (gate)
        {
            var snap = current;
            var exists = snap.VolumesById.ContainsKey(volume.Id);
            IReadOnlyList<StorageVolume> nextVolumes = exists
                ? [.. snap.Volumes.Select(v => string.Equals(v.Id, volume.Id, StringComparison.Ordinal) ? volume : v)]
                : [.. snap.Volumes, volume];

            var nextStorages = storage is not null || !snap.Storages.ContainsKey(volume.Id)
                ? WithStorage(snap.Storages, volume.Id, storage ?? factory(volume))
                : snap.Storages;

            current = CreateSnapshot(nextVolumes, nextStorages);
        }
    }

    public bool TryUpdateStatus(
        string volumeId,
        VolumeStatus status,
        long? availableBytes = null,
        long? capacityBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);

        lock (gate)
        {
            var snap = current;
            if (!snap.VolumesById.TryGetValue(volumeId, out var existing))
                return false;

            var updated = existing with
            {
                Status = status,
                AvailableBytes = availableBytes ?? existing.AvailableBytes,
                CapacityBytes = capacityBytes ?? existing.CapacityBytes,
            };

            IReadOnlyList<StorageVolume> nextVolumes =
                [.. snap.Volumes.Select(v => string.Equals(v.Id, volumeId, StringComparison.Ordinal) ? updated : v)];

            current = CreateSnapshot(nextVolumes, snap.Storages);
            return true;
        }
    }

    public bool RemoveVolume(string volumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);

        lock (gate)
        {
            var snap = current;
            if (!snap.VolumesById.ContainsKey(volumeId))
                return false;

            IReadOnlyList<StorageVolume> nextVolumes =
                [.. snap.Volumes.Where(v => !string.Equals(v.Id, volumeId, StringComparison.Ordinal))];

            if (nextVolumes.Count == 0)
                throw new InvalidOperationException("At least one storage volume must remain registered.");

            var nextStorages = WithoutStorage(snap.Storages, volumeId);
            current = CreateSnapshot(nextVolumes, nextStorages);
            return true;
        }
    }

    private static RegistrySnapshot BuildInitialSnapshot(
        IReadOnlyList<StorageVolume> initialVolumes,
        Func<StorageVolume, IVolumeStorage> storageFactory)
    {
        ArgumentNullException.ThrowIfNull(initialVolumes);
        if (initialVolumes.Count == 0)
            throw new ArgumentException("At least one storage volume must be configured.", nameof(initialVolumes));

        Dictionary<string, IVolumeStorage> storages = new(initialVolumes.Count, StringComparer.Ordinal);
        foreach (var vol in initialVolumes)
        {
            if (!storages.TryAdd(vol.Id, storageFactory(vol)))
                throw new ArgumentException($"Duplicate volume id '{vol.Id}'.", nameof(initialVolumes));
        }

        if (!initialVolumes.Any(v => v.IsWritable))
            throw new InvalidOperationException("No writable storage volume configured.");

        return CreateSnapshot(initialVolumes, storages.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static RegistrySnapshot CreateSnapshot(
        IReadOnlyList<StorageVolume> volumeList,
        FrozenDictionary<string, IVolumeStorage> storages)
    {
        IReadOnlyList<StorageVolume> volumes = [.. volumeList];
        var volumesById = volumes.ToFrozenDictionary(v => v.Id, StringComparer.Ordinal);
        var pools = volumes
            .GroupBy(v => v.Pool, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(
                g => g.Key,
                g => (IReadOnlyList<StorageVolume>)[.. g],
                StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<StorageVolume> writable = [.. volumes.Where(v => v.IsWritable)];
        IReadOnlyList<StorageVolume> readPriority =
        [
            .. volumes
                .Where(v => v.IsReadable)
                .OrderBy(GetReadTier)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
        ];

        var defaultIngest = writable.FirstOrDefault(v => v.IsIngest && !v.IsOnDemand)
            ?? (writable is [var firstWritable, ..]
                ? firstWritable
                : throw new InvalidOperationException("No writable storage volume configured."));

        IReadOnlyList<IVolumeStorage> writableStorages = [.. writable.Select(v => storages[v.Id])];
        IReadOnlyList<IVolumeStorage> readPriorityStorages = [.. readPriority.Select(v => storages[v.Id])];
        var readableStorages = readPriority.ToFrozenDictionary(v => v.Id, v => storages[v.Id], StringComparer.Ordinal);
        var defaultIngestStorage = storages[defaultIngest.Id];

        return new(
            volumes,
            writable,
            readPriority,
            writableStorages,
            readPriorityStorages,
            defaultIngest,
            defaultIngestStorage,
            volumesById,
            storages,
            readableStorages,
            pools);
    }

    private static int GetReadTier(StorageVolume volume)
    {
        if (volume.IsOnDemand || volume.IsRemote) return 2;
        if (volume.IsIngest) return 0;
        return 1;
    }

    private static FrozenDictionary<string, IVolumeStorage> WithStorage(
        FrozenDictionary<string, IVolumeStorage> existing,
        string volumeId,
        IVolumeStorage storage)
    {
        Dictionary<string, IVolumeStorage> copy = new(existing, StringComparer.Ordinal)
        {
            [volumeId] = storage
        };
        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, IVolumeStorage> WithoutStorage(
        FrozenDictionary<string, IVolumeStorage> existing,
        string volumeId)
    {
        Dictionary<string, IVolumeStorage> copy = new(existing, StringComparer.Ordinal);
        copy.Remove(volumeId);
        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record RegistrySnapshot(
        IReadOnlyList<StorageVolume> Volumes,
        IReadOnlyList<StorageVolume> WritableVolumes,
        IReadOnlyList<StorageVolume> ReadPriorityVolumes,
        IReadOnlyList<IVolumeStorage> WritableStorages,
        IReadOnlyList<IVolumeStorage> ReadPriorityStorages,
        StorageVolume DefaultIngestVolume,
        IVolumeStorage DefaultIngestStorage,
        FrozenDictionary<string, StorageVolume> VolumesById,
        FrozenDictionary<string, IVolumeStorage> Storages,
        FrozenDictionary<string, IVolumeStorage> ReadableStorages,
        FrozenDictionary<string, IReadOnlyList<StorageVolume>> Pools);
}
