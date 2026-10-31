using System.Text.Json.Serialization;

namespace Vessel3.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GossipMessage))]
[JsonSerializable(typeof(PeerNodeState))]
[JsonSerializable(typeof(AdvertisedVolume))]
[JsonSerializable(typeof(IReadOnlyList<PeerNodeState>))]
[JsonSerializable(typeof(IReadOnlyList<AdvertisedVolume>))]
[JsonSerializable(typeof(ClusterCatalogSnapshot))]
[JsonSerializable(typeof(BucketCatalogEntry))]
[JsonSerializable(typeof(BucketConfigSnapshot))]
[JsonSerializable(typeof(BucketEventsBatch))]
[JsonSerializable(typeof(VersionEvent))]
[JsonSerializable(typeof(PutEvent))]
[JsonSerializable(typeof(DeleteMarkerEvent))]
[JsonSerializable(typeof(HardDeleteEvent))]
[JsonSerializable(typeof(PutTaggingEvent))]
[JsonSerializable(typeof(PutRetentionEvent))]
[JsonSerializable(typeof(PutLegalHoldEvent))]
[JsonSerializable(typeof(IdentityReplicationSnapshot))]
[JsonSerializable(typeof(ReplicatedUserEntry))]
[JsonSerializable(typeof(AccessKey))]
[JsonSerializable(typeof(DeletedUserTombstone))]
[JsonSerializable(typeof(UploadSessionSnapshot))]
[JsonSerializable(typeof(StagedSession))]
[JsonSerializable(typeof(StagedChunk))]
[JsonSerializable(typeof(ClusterGcReferencesResponse))]
internal sealed partial class GossipJsonContext : JsonSerializerContext;
