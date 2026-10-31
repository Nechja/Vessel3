using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal sealed class VolumeRegistry : IVolumeRegistry
{
    private readonly Func<StorageVolume, IVolumeStorage> localStorageFactory;
    private readonly Func<StorageVolume, IBlobReader> remoteReaderFactory;
    private readonly Lock gate = new();
    private long lastTopologyVersion;
    private volatile RegistrySnapshot current;

    public VolumeRegistry(
        IReadOnlyList<StorageVolume> volumes,
        IFileSync fileSync,
        Func<StorageVolume, IVolumeStorage>? storageFactory = null,
        Func<StorageVolume, IBlobReader>? remoteFactory = null)
    {
        localStorageFactory = storageFactory ?? (v => new LocalDiskVolumeStorage(v, fileSync));
        remoteReaderFactory = remoteFactory ?? (static v => new NullRemoteBlobReader(v.Id));
        current = BuildInitialSnapshot(volumes, localStorageFactory);
    }

    public IReadOnlyList<StorageVolume> Volumes => current.Volumes;
    public IReadOnlyList<StorageVolume> WritableVolumes => current.WritableVolumes;
    public IReadOnlyList<StorageVolume> ReadPriorityVolumes => current.ReadPriorityVolumes;
    public IReadOnlyList<AdvertisedVolume> LocalAdvertisedVolumes => current.LocalAdvertisedVolumes;
    public IReadOnlyList<IVolumeStorage> LocalStorages => current.LocalStorages;
    public IReadOnlyList<IVolumeStorage> WritableStorages => current.WritableStorages;
    public IReadOnlyList<IVolumeStorage> MirrorTargetStorages => current.MirrorTargetStorages;
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

    public bool TryGetOnlineLocalStorage(string volumeId, [NotNullWhen(true)] out IVolumeStorage? storage) =>
        current.OnlineLocalStorages.TryGetValue(volumeId, out storage);

    public bool TryGetOnlineReader(string volumeId, [NotNullWhen(true)] out IBlobReader? reader) =>
        current.OnlineReaders.TryGetValue(volumeId, out reader);

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
                ?? (snap.Storages.TryGetValue(volume.Id, out var currentStorage) ? currentStorage : localStorageFactory(volume));

            var nextStorages = WithEntry(snap.Storages, volume.Id, resolvedStorage);
            var nextReaders = WithEntry(snap.Readers, volume.Id, resolvedStorage);

            current = CreateSnapshot(nextVolumes, nextStorages, nextReaders);
        }
    }

    public void UpsertRemoteVolume(StorageVolume volume, IBlobReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var normalizedCaps = StorageVolume.NormalizeRemoteCapabilities(volume.Capabilities);
        var remoteVolume = volume.Capabilities == normalizedCaps
            ? volume
            : volume with { Capabilities = normalizedCaps };

        lock (gate)
        {
            var snap = current;
            var resolvedReader = reader
                ?? (snap.VolumesById.TryGetValue(remoteVolume.Id, out var prev)
                    && string.Equals(prev.Path, remoteVolume.Path, StringComparison.Ordinal)
                    && snap.Readers.TryGetValue(remoteVolume.Id, out var existingReader)
                        ? existingReader
                        : remoteReaderFactory(remoteVolume));

            if (snap.VolumesById.TryGetValue(remoteVolume.Id, out var existing)
                && existing == remoteVolume
                && ReferenceEquals(snap.Readers.GetValueOrDefault(remoteVolume.Id), resolvedReader))
            {
                return;
            }

            IReadOnlyList<StorageVolume> nextVolumes = existing is not null
                ? [.. snap.Volumes.Select(v => string.Equals(v.Id, remoteVolume.Id, StringComparison.Ordinal) ? remoteVolume : v)]
                : [.. snap.Volumes, remoteVolume];

            var nextReaders = WithEntry(snap.Readers, remoteVolume.Id, resolvedReader);
            var nextStorages = resolvedReader is IVolumeStorage vs
                ? WithEntry(snap.Storages, remoteVolume.Id, vs)
                : snap.Storages;

            current = CreateSnapshot(nextVolumes, nextStorages, nextReaders);
        }
    }

    public void SyncPeerTopology(PeerTopologySnapshot snapshot)
    {
        if (snapshot.Peers is null)
            return;

        lock (gate)
        {
            if (snapshot.Version > 0 && snapshot.Version <= lastTopologyVersion)
                return;

            if (snapshot.Version > 0)
                lastTopologyVersion = snapshot.Version;

            var snap = current;
            List<StorageVolume> nextVolumes = new(snap.Volumes.Count + snapshot.Peers.Count);
            foreach (var vol in snap.Volumes)
            {
                if (!vol.IsRemote)
                    nextVolumes.Add(vol);
            }

            Dictionary<string, IBlobReader>? mutatedReaders = null;
            HashSet<string> activeRemoteIds = new(StringComparer.Ordinal);

            foreach (var peer in snapshot.Peers)
            {
                var statusResolver = SwimProtocol.EffectiveVolumeStatus[peer.Status];
                foreach (var adv in peer.Volumes)
                {
                    var remoteVol = StorageVolume.CreateRemote(peer, adv, statusResolver(adv.Status));
                    if (!activeRemoteIds.Add(remoteVol.Id))
                        continue;

                    nextVolumes.Add(remoteVol);

                    if (snap.VolumesById.TryGetValue(remoteVol.Id, out var existingVol)
                        && string.Equals(existingVol.Path, remoteVol.Path, StringComparison.Ordinal)
                        && snap.Readers.ContainsKey(remoteVol.Id))
                    {
                        continue;
                    }

                    mutatedReaders ??= new(snap.Readers, StringComparer.Ordinal);
                    mutatedReaders[remoteVol.Id] = remoteReaderFactory(remoteVol);
                }
            }

            foreach (var oldVol in snap.Volumes)
            {
                if (!oldVol.IsRemote || activeRemoteIds.Contains(oldVol.Id))
                    continue;

                mutatedReaders ??= new(snap.Readers, StringComparer.Ordinal);
                mutatedReaders.Remove(oldVol.Id);
            }

            if (mutatedReaders is null && VolumesListEqual(snap.Volumes, nextVolumes))
                return;

            var nextReaders = mutatedReaders is not null
                ? mutatedReaders.ToFrozenDictionary(StringComparer.Ordinal)
                : snap.Readers;

            current = CreateSnapshot(nextVolumes, snap.Storages, nextReaders);
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

        if (!initialVolumes.Any(v => v.IsLocalReplicaWritable))
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
        IReadOnlyList<StorageVolume> mirrorTargets = [.. volumes.Where(v => v.IsLocalMirrorTarget)];
        IReadOnlyList<StorageVolume> readPriority =
        [
            .. volumes
                .Where(v => v.IsReadable)
                .OrderBy(GetReadTier)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
        ];

        IReadOnlyList<AdvertisedVolume> localAdvertised =
            [.. volumes.Where(v => !v.IsRemote).Select(v => v.ToAdvertised())];

        var defaultIngest = writable.FirstOrDefault(v => v.IsIngest && !v.IsOnDemand)
            ?? (writable is [var firstWritable, ..]
                ? firstWritable
                : mirrorTargets is [var firstMirror, ..]
                    ? firstMirror
                    : volumes.FirstOrDefault(v => !v.IsRemote)
                        ?? throw new InvalidOperationException("No writable storage volume configured."));

        List<IVolumeStorage> localStorages = new(readPriority.Count);
        List<IVolumeStorage> readPriorityStorages = new(readPriority.Count);
        List<IBlobReader> readPriorityReaders = new(readPriority.Count);
        Dictionary<string, IVolumeStorage> onlineLocalStorages = new(readPriority.Count, StringComparer.Ordinal);
        Dictionary<string, IBlobReader> onlineReaders = new(readPriority.Count, StringComparer.Ordinal);

        foreach (var v in readPriority)
        {
            var reader = readers[v.Id];
            readPriorityReaders.Add(reader);
            onlineReaders[v.Id] = reader;

            if (storages.TryGetValue(v.Id, out var st))
            {
                readPriorityStorages.Add(st);
                if (!v.IsRemote)
                {
                    localStorages.Add(st);
                    onlineLocalStorages[v.Id] = st;
                }
            }
        }

        List<IVolumeStorage> writableStorages = new(writable.Count);
        foreach (var v in writable)
        {
            if (storages.TryGetValue(v.Id, out var st))
                writableStorages.Add(st);
        }

        List<IVolumeStorage> mirrorTargetStorages = new(mirrorTargets.Count);
        foreach (var v in mirrorTargets)
        {
            if (storages.TryGetValue(v.Id, out var st))
                mirrorTargetStorages.Add(st);
        }

        var defaultIngestStorage = storages[defaultIngest.Id];

        return new(
            volumes,
            writable,
            readPriority,
            localAdvertised,
            localStorages,
            writableStorages,
            mirrorTargetStorages,
            readPriorityStorages,
            readPriorityReaders,
            defaultIngest,
            defaultIngestStorage,
            volumesById,
            storages,
            readers,
            onlineLocalStorages.ToFrozenDictionary(StringComparer.Ordinal),
            onlineReaders.ToFrozenDictionary(StringComparer.Ordinal),
            pools);
    }

    private static bool VolumesListEqual(IReadOnlyList<StorageVolume> a, IReadOnlyList<StorageVolume> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
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

    private sealed class NullRemoteBlobReader(string volumeId) : IBlobReader
    {
        public string VolumeId => volumeId;
        public Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default) =>
            Task.FromResult<Result<Stream>>(new NotFoundError($"blob {sha}"));
        public Task<bool> BlobExists(string sha, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed record RegistrySnapshot(
        IReadOnlyList<StorageVolume> Volumes,
        IReadOnlyList<StorageVolume> WritableVolumes,
        IReadOnlyList<StorageVolume> ReadPriorityVolumes,
        IReadOnlyList<AdvertisedVolume> LocalAdvertisedVolumes,
        IReadOnlyList<IVolumeStorage> LocalStorages,
        IReadOnlyList<IVolumeStorage> WritableStorages,
        IReadOnlyList<IVolumeStorage> MirrorTargetStorages,
        IReadOnlyList<IVolumeStorage> ReadPriorityStorages,
        IReadOnlyList<IBlobReader> ReadPriorityReaders,
        StorageVolume DefaultIngestVolume,
        IVolumeStorage DefaultIngestStorage,
        FrozenDictionary<string, StorageVolume> VolumesById,
        FrozenDictionary<string, IVolumeStorage> Storages,
        FrozenDictionary<string, IBlobReader> Readers,
        FrozenDictionary<string, IVolumeStorage> OnlineLocalStorages,
        FrozenDictionary<string, IBlobReader> OnlineReaders,
        FrozenDictionary<string, IReadOnlyList<StorageVolume>> Pools);
}
