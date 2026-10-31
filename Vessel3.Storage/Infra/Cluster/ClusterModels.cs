using System.Text.Json.Serialization;

namespace Vessel3.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<PeerStatus>))]
public enum PeerStatus : byte
{
    Alive = 0,
    Suspect = 1,
    Dead = 2,
    Left = 3
}

public sealed record AdvertisedVolume(
    string VolumeId,
    string Pool,
    VolumeCapabilities Capabilities,
    VolumeStatus Status,
    long? CapacityBytes = null,
    long? AvailableBytes = null);

public sealed record PeerNodeState(
    string NodeId,
    string Endpoint,
    long Incarnation,
    PeerStatus Status,
    DateTimeOffset LastSeenUtc,
    IReadOnlyList<AdvertisedVolume> Volumes)
{
    public bool HasSameTopology(PeerNodeState? other) =>
        other is not null
        && Status == other.Status
        && string.Equals(NodeId, other.NodeId, StringComparison.Ordinal)
        && string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal)
        && VolumesEqual(Volumes, other.Volumes);

    public bool HasSameState(PeerNodeState? other) =>
        other is not null
        && Incarnation == other.Incarnation
        && HasSameTopology(other);

    public bool Equals(PeerNodeState? other) =>
        other is not null
        && LastSeenUtc.Equals(other.LastSeenUtc)
        && HasSameState(other);

    public override int GetHashCode()
    {
        HashCode hc = default;
        hc.Add(NodeId, StringComparer.Ordinal);
        hc.Add(Endpoint, StringComparer.Ordinal);
        hc.Add(Incarnation);
        hc.Add(Status);
        hc.Add(LastSeenUtc);
        for (var i = 0; i < Volumes.Count; i++)
            hc.Add(Volumes[i]);
        return hc.ToHashCode();
    }

    internal static bool VolumesEqual(IReadOnlyList<AdvertisedVolume> a, IReadOnlyList<AdvertisedVolume> b)
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
}

public readonly record struct PeerTopologySnapshot(
    long Version,
    IReadOnlyList<PeerNodeState> Peers);

public sealed record GossipMessage(
    string SenderNodeId,
    string SenderEndpoint,
    long SenderIncarnation,
    IReadOnlyList<PeerNodeState> Members);

internal sealed record ClusterCatalogSnapshot(
    string NodeId,
    long IamGeneration,
    IReadOnlyList<BucketCatalogEntry> Buckets);

internal sealed record BucketEventsBatch(
    string Bucket,
    long FirstAvailableSeq,
    long LatestSeq,
    bool RequiresSnapshot,
    IReadOnlyList<VersionEvent> Events);

internal sealed record ClusterGcReferencesResponse(
    IReadOnlyList<string> ReferencedShas);

public sealed record ClusterMembershipOptions(
    string NodeId,
    string LocalEndpoint,
    IReadOnlyList<string> SeedEndpoints,
    TimeSpan GossipInterval,
    TimeSpan SuspectTimeout,
    TimeSpan DeadTimeout,
    string? ClusterSecret = null)
{
    public string NormalizedLocalEndpoint { get; } = LocalEndpoint.TrimEnd('/');

    public IReadOnlyList<string> NormalizedSeedEndpoints { get; } =
        [.. SeedEndpoints.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.TrimEnd('/'))];

    public bool IsClusterEnabled =>
        !string.IsNullOrWhiteSpace(NormalizedLocalEndpoint) || NormalizedSeedEndpoints.Count > 0;
}
