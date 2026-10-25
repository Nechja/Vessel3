namespace Vessel3.Storage;

internal sealed class LocalVolumeAdvertiser(IVolumeRegistry volumeRegistry) : ILocalVolumeAdvertiser
{
    public IReadOnlyList<AdvertisedVolume> GetAdvertisedVolumes()
    {
        var volumes = volumeRegistry.Volumes;
        List<AdvertisedVolume> list = new(volumes.Count);

        foreach (var v in volumes)
        {
            if (v.IsRemote)
                continue;

            list.Add(new(
                v.Id,
                v.Pool,
                v.Capabilities,
                v.Status,
                v.CapacityBytes,
                v.AvailableBytes));
        }

        return list;
    }
}
