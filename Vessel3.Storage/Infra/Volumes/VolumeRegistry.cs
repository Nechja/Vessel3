namespace Vessel3.Storage;

internal sealed class VolumeRegistry : IVolumeRegistry
{
    private readonly Dictionary<string, StorageVolume> volumesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IVolumeStorage> storages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StorageVolume>> pools = new(StringComparer.OrdinalIgnoreCase);

    public VolumeRegistry(IReadOnlyList<StorageVolume> volumes, IFileSync fileSync, Func<StorageVolume, IVolumeStorage>? storageFactory = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (volumes.Count == 0)
            throw new ArgumentException("At least one storage volume must be configured.", nameof(volumes));

        Volumes = volumes;
        var factory = storageFactory ?? (v => new LocalDiskVolumeStorage(v, fileSync));

        foreach (var vol in volumes)
        {
            if (!volumesById.TryAdd(vol.Id, vol))
                throw new ArgumentException($"Duplicate volume id '{vol.Id}'.", nameof(volumes));

            storages[vol.Id] = factory(vol);

            if (!pools.TryGetValue(vol.Pool, out var poolList))
            {
                poolList = [];
                pools[vol.Pool] = poolList;
            }
            poolList.Add(vol);
        }

        WritableVolumes = [.. volumes.Where(v => v.IsWritable)];

        ReadPriorityVolumes = [.. volumes
            .OrderBy(v => v.IsOnDemand || v.IsRemote ? 2 : (v.IsIngest ? 0 : 1))
            .ThenBy(v => v.Id, StringComparer.Ordinal)];

        DefaultIngestVolume = volumes.FirstOrDefault(v => v.IsIngest && v.IsWritable && !v.IsOnDemand)
            ?? volumes.FirstOrDefault(v => v.IsWritable)
            ?? throw new InvalidOperationException("No writable storage volume configured.");
    }

    public IReadOnlyList<StorageVolume> Volumes { get; }
    public IReadOnlyList<StorageVolume> WritableVolumes { get; }
    public IReadOnlyList<StorageVolume> ReadPriorityVolumes { get; }
    public StorageVolume DefaultIngestVolume { get; }

    public StorageVolume? GetVolume(string id) =>
        volumesById.GetValueOrDefault(id);

    public IReadOnlyList<StorageVolume> GetPool(string pool) =>
        pools.GetValueOrDefault(pool) ?? [];

    public IVolumeStorage GetStorage(string volumeId) =>
        storages.TryGetValue(volumeId, out var storage)
            ? storage
            : throw new KeyNotFoundException($"Storage volume '{volumeId}' not found.");
}
