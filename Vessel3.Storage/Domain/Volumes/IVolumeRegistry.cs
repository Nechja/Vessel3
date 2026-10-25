using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal interface IVolumeRegistry
{
    IReadOnlyList<StorageVolume> Volumes { get; }
    IReadOnlyList<StorageVolume> WritableVolumes { get; }
    IReadOnlyList<StorageVolume> ReadPriorityVolumes { get; }
    IReadOnlyList<AdvertisedVolume> LocalAdvertisedVolumes { get; }
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
    bool TryGetOnlineLocalStorage(string volumeId, [NotNullWhen(true)] out IVolumeStorage? storage);
    bool TryGetOnlineReader(string volumeId, [NotNullWhen(true)] out IBlobReader? reader);
    void UpsertVolume(StorageVolume volume, IVolumeStorage? storage = null);
    void UpsertRemoteVolume(StorageVolume volume, IBlobReader? reader = null);
    void SyncPeerTopology(PeerTopologySnapshot snapshot);
    bool TryUpdateStatus(string volumeId, VolumeStatus status, long? availableBytes = null, long? capacityBytes = null);
    bool RemoveVolume(string volumeId);
}
