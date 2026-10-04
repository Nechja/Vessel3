namespace Vessel3.Storage;

internal interface IVolumeRegistry
{
    IReadOnlyList<StorageVolume> Volumes { get; }
    IReadOnlyList<StorageVolume> ReadPriorityVolumes { get; }
    StorageVolume DefaultIngestVolume { get; }
    StorageVolume? GetVolume(string id);
    IReadOnlyList<StorageVolume> GetPool(string pool);
    IVolumeStorage GetStorage(string volumeId);
}
