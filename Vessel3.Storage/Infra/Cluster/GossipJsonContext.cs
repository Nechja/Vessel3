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
internal sealed partial class GossipJsonContext : JsonSerializerContext;
