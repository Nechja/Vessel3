namespace Vessel3.Protocols.Oci;

public sealed record ContainerRepo(string Name, DateTimeOffset CreatedAt, string? OwnerId = null);

public sealed record ContainerTag(string Repo, string Name, string ManifestDigest, DateTimeOffset UpdatedAt);

public sealed record ContainerManifest(string Digest, string MediaType, long Size, byte[] Content, DateTimeOffset CreatedAt);

public sealed record ContainerUploadSession(string Id, string Repo, string TempFilePath, long BytesReceived, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record PutManifestOutcome(string Digest, bool Created, IReadOnlyList<string> LayerDigests);
