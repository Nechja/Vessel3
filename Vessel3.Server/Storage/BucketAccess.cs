using System.Text.Json.Serialization;

namespace Vessel3.Server.Storage;

internal sealed record BucketAccess(bool PublicRead, bool ReadOnly)
{
    public static BucketAccess Private => new(false, false);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(BucketAccess))]
internal sealed partial class BucketAccessJsonContext : JsonSerializerContext;
