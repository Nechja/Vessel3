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
    public IReadOnlyList<IVolumeStorage> LocalStorages => current.LocalStorages;
    public IReadOnlyList<IVolumeStorage> WritableStorages => current.WritableStorages;
    public IReadOnlyList<IVolumeStorage> ReadPriorityStorages => current.ReadPriorityStorages;
    public IReadOnlyList<IBlobReader> ReadPriorityReaders => current.ReadPriorityReaders;
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

    public IBlobReader GetReader(string volumeId) =>
        current.Readers.TryGetValue(volumeId, out var reader)
            ? reader
            : throw new KeyNotFoundException($"Volume reader '{volumeId}' not found.");

    public bool TryGetReadableStorage(string volumeId, [NotNullWhen(true)] out IVolumeStorage? storage) =>
        current.ReadableStorages.TryGetValue(volumeId, out storage);

    public bool TryGetReadableReader(string volumeId, [NotNullWhen(true)] out IBlobReader? reader) =>
        current.ReadableReaders.TryGetValue(volumeId, out reader);

    public void UpsertVolume(StorageVolume volume, IVolumeStorage? storage = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        lock (gate)
        {
            var snap = current;
            if (snap.VolumesById.TryGetValue(volume.Id, out var existing)
                && existing == volume
                && (storage is null || ReferenceEquals(snap.Storages.GetValueOrDefault(volume.Id), storage)))
            {
                return;
            }

            IReadOnlyList<StorageVolume> nextVolumes = existing is not null
                ? [.. snap.Volumes.Select(v => string.Equals(v.Id, volume.Id, StringComparison.Ordinal) ? volume : v)]
                : [.. snap.Volumes, volume];

            var resolvedStorage = storage
                ?? (snap.Storages.TryGetValue(volume.Id, out var currentStorage) ? currentStorage : factory(volume));

            var nextStorages = WithEntry(snap.Storages, volume.Id, resolvedStorage);
            var nextReaders = WithEntry(snap.Readers, volume.Id, resolvedStorage);

            current = CreateSnapshot(nextVolumes, nextStorages, nextReaders);
        }
    }

    public void UpsertRemoteVolume(StorageVolume volume, IBlobReader reader)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(reader);

        var remoteVolume = volume.IsRemote
            ? volume
            : volume with { Capabilities = (volume.Capabilities & ~VolumeCapabilities.Ingest) | VolumeCapabilities.Remote };

        lock (gate)
        {
            var snap = current;
            if (snap.VolumesById.TryGetValue(remoteVolume.Id, out var existing)
                && existing == remoteVolume
                && ReferenceEquals(snap.Readers.GetValueOrDefault(remoteVolume.Id), reader))
            {
                return;
            }

            IReadOnlyList<StorageVolume> nextVolumes = existing is not null
                ? [.. snap.Volumes.Select(v => string.Equals(v.Id, remoteVolume.Id, StringComparison.Ordinal) ? remoteVolume : v)]
                : [.. snap.Volumes, remoteVolume];

            var nextReaders = WithEntry(snap.Readers, remoteVolume.Id, reader);
            var nextStorages = reader is IVolumeStorage vs
                ? WithEntry(snap.Storages, remoteVolume.Id, vs)
                : snap.Storages;

            current = CreateSnapshot(nextVolumes, nextStorages, nextReaders);
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

            if (existing == updated)
                return true;

            IReadOnlyList<StorageVolume> nextVolumes =
                [.. snap.Volumes.Select(v => string.Equals(v.Id, volumeId, StringComparison.Ordinal) ? updated : v)];

            current = CreateSnapshot(nextVolumes, snap.Storages, snap.Readers);
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

            var nextStorages = WithoutEntry(snap.Storages, volumeId);
            var nextReaders = WithoutEntry(snap.Readers, volumeId);
            current = CreateSnapshot(nextVolumes, nextStorages, nextReaders);
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
        Dictionary<string, IBlobReader> readers = new(initialVolumes.Count, StringComparer.Ordinal);
        foreach (var vol in initialVolumes)
        {
            var storage = storageFactory(vol);
            if (!storages.TryAdd(vol.Id, storage))
                throw new ArgumentException($"Duplicate volume id '{vol.Id}'.", nameof(initialVolumes));
            readers[vol.Id] = storage;
        }

        if (!initialVolumes.Any(v => v.IsWritable))
            throw new InvalidOperationException("No writable storage volume configured.");

        return CreateSnapshot(
            initialVolumes,
            storages.ToFrozenDictionary(StringComparer.Ordinal),
            readers.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static RegistrySnapshot CreateSnapshot(
        IReadOnlyList<StorageVolume> volumeList,
        FrozenDictionary<string, IVolumeStorage> storages,
        FrozenDictionary<string, IBlobReader> readers)
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

        IReadOnlyList<IVolumeStorage> localStorages =
            [.. readPriority.Where(v => !v.IsRemote && storages.ContainsKey(v.Id)).Select(v => storages[v.Id])];
        IReadOnlyList<IVolumeStorage> writableStorages =
            [.. writable.Where(v => storages.ContainsKey(v.Id)).Select(v => storages[v.Id])];
        IReadOnlyList<IVolumeStorage> readPriorityStorages =
            [.. readPriority.Where(v => storages.ContainsKey(v.Id)).Select(v => storages[v.Id])];
        IReadOnlyList<IBlobReader> readPriorityReaders =
            [.. readPriority.Select(v => readers[v.Id])];

        var readableStorages = readPriority
            .Where(v => storages.ContainsKey(v.Id))
            .ToFrozenDictionary(v => v.Id, v => storages[v.Id], StringComparer.Ordinal);
        var readableReaders = readPriority
            .ToFrozenDictionary(v => v.Id, v => readers[v.Id], StringComparer.Ordinal);
        var defaultIngestStorage = storages[defaultIngest.Id];

        return new(
            volumes,
            writable,
            readPriority,
            localStorages,
            writableStorages,
            readPriorityStorages,
            readPriorityReaders,
            defaultIngest,
            defaultIngestStorage,
            volumesById,
            storages,
            readers,
            readableStorages,
            readableReaders,
            pools);
    }

    private static int GetReadTier(StorageVolume volume)
    {
        if (volume.IsOnDemand || volume.IsRemote) return 2;
        if (volume.IsIngest) return 0;
        return 1;
    }

    private static FrozenDictionary<string, T> WithEntry<T>(
        FrozenDictionary<string, T> existing,
        string key,
        T value)
    {
        Dictionary<string, T> copy = new(existing, StringComparer.Ordinal)
        {
            [key] = value
        };
        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, T> WithoutEntry<T>(
        FrozenDictionary<string, T> existing,
        string key)
    {
        if (!existing.ContainsKey(key))
            return existing;

        Dictionary<string, T> copy = new(existing, StringComparer.Ordinal);
        copy.Remove(key);
        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record RegistrySnapshot(
        IReadOnlyList<StorageVolume> Volumes,
        IReadOnlyList<StorageVolume> WritableVolumes,
        IReadOnlyList<StorageVolume> ReadPriorityVolumes,
        IReadOnlyList<IVolumeStorage> LocalStorages,
        IReadOnlyList<IVolumeStorage> WritableStorages,
        IReadOnlyList<IVolumeStorage> ReadPriorityStorages,
        IReadOnlyList<IBlobReader> ReadPriorityReaders,
        StorageVolume DefaultIngestVolume,
        IVolumeStorage DefaultIngestStorage,
        FrozenDictionary<string, StorageVolume> VolumesById,
        FrozenDictionary<string, IVolumeStorage> Storages,
        FrozenDictionary<string, IBlobReader> Readers,
        FrozenDictionary<string, IVolumeStorage> ReadableStorages,
        FrozenDictionary<string, IBlobReader> ReadableReaders,
        FrozenDictionary<string, IReadOnlyList<StorageVolume>> Pools);
}
