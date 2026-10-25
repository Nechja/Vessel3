using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal interface IVolumeRegistry
{
    IReadOnlyList<StorageVolume> Volumes { get; }
    IReadOnlyList<StorageVolume> WritableVolumes { get; }
    IReadOnlyList<StorageVolume> ReadPriorityVolumes { get; }
    IReadOnlyList<IVolumeStorage> LocalStorages { get; }
    IReadOnlyList<IVolumeStorage> WritableStorages { get; }
    IReadOnlyList<IVolumeStorage> ReadPriorityStorages { get; }
    IReadOnlyList<IBlobReader> ReadPriorityReaders { get; }
    StorageVolume DefaultIngestVolume { get; }
    IVolumeStorage DefaultIngestStorage { get; }
    StorageVolume? GetVolume(string id);
    IReadOnlyList<StorageVolume> GetPool(string pool);
    IVolumeStorage GetStorage(string volumeId);
    IBlobReader GetReader(string volumeId);
    bool TryGetReadableStorage(string volumeId, [NotNullWhen(true)] out IVolumeStorage? storage);
    bool TryGetReadableReader(string volumeId, [NotNullWhen(true)] out IBlobReader? reader);
    void UpsertVolume(StorageVolume volume, IVolumeStorage? storage = null);
    void UpsertRemoteVolume(StorageVolume volume, IBlobReader reader);
    bool TryUpdateStatus(string volumeId, VolumeStatus status, long? availableBytes = null, long? capacityBytes = null);
    bool RemoveVolume(string volumeId);
}
