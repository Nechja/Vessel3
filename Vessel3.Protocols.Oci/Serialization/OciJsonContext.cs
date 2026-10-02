using System.Text.Json.Serialization;

namespace Vessel3.Protocols.Oci;

public readonly record struct OciDescriptorDto(
    string? MediaType,
    long? Size,
    string? Digest,
    IReadOnlyList<string>? Urls,
    IReadOnlyDictionary<string, string>? Annotations);

public readonly record struct OciManifestDto(
    int? SchemaVersion,
    string? MediaType,
    OciDescriptorDto? Config,
    IReadOnlyList<OciDescriptorDto>? Layers,
    IReadOnlyDictionary<string, string>? Annotations);

public readonly record struct OciIndexDto(
    int? SchemaVersion,
    string? MediaType,
    IReadOnlyList<OciDescriptorDto>? Manifests,
    IReadOnlyDictionary<string, string>? Annotations);

public readonly record struct TagsListDto(string Name, IReadOnlyList<string> Tags);

public readonly record struct CatalogListDto(IReadOnlyList<string> Repositories);

public readonly record struct TokenResponseDto(
    string Token,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("issued_at")] string? IssuedAt);

public readonly record struct OciErrorItemDto(string Code, string Message, string? Detail);

public readonly record struct OciErrorResponseDto(IReadOnlyList<OciErrorItemDto> Errors);

public readonly record struct ContainerRepoSummaryDto(string Name, DateTimeOffset CreatedAt, string? OwnerId, int TagCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OciDescriptorDto))]
[JsonSerializable(typeof(IReadOnlyList<OciDescriptorDto>))]
[JsonSerializable(typeof(OciManifestDto))]
[JsonSerializable(typeof(OciIndexDto))]
[JsonSerializable(typeof(TagsListDto))]
[JsonSerializable(typeof(CatalogListDto))]
[JsonSerializable(typeof(TokenResponseDto))]
[JsonSerializable(typeof(OciErrorItemDto))]
[JsonSerializable(typeof(OciErrorResponseDto))]
[JsonSerializable(typeof(ContainerRepoSummaryDto))]
[JsonSerializable(typeof(IReadOnlyList<ContainerRepoSummaryDto>))]
[JsonSerializable(typeof(List<ContainerRepoSummaryDto>))]
internal sealed partial class OciJsonContext : JsonSerializerContext;
