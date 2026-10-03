namespace Vessel3.Storage;

internal sealed record ChunkStagerOptions(string Root);

internal sealed record StagedSession(
    string SessionId,
    string Bucket,
    string Key,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

internal sealed record StagedChunk(
    string Token,
    string BlobSha,
    string Md5,
    long Size,
    string? Crc32 = null,
    string? Crc32C = null,
    string? Sha1 = null,
    DateTimeOffset StagedAt = default);

internal sealed record CommitChunksOutcome(
    string VersionId,
    long TotalSize);

internal interface IChunkStager
{
    Result<StagedSession> CreateSession(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata);
    Task<Result<StagedChunk>> StageChunk(string sessionId, string chunkToken, Stream body, long? declaredSize, DeclaredChecksums declaredChecksums, CancellationToken ct);
    Result<IReadOnlyList<StagedChunk>> ListChunks(string sessionId);
    Result<StagedSession> GetSession(string sessionId);
    IEnumerable<StagedSession> ListSessions(string bucket);
    Result AbortSession(string sessionId);
    Task<Result<CommitChunksOutcome>> Commit(
        string sessionId,
        IReadOnlyList<MultipartPart> orderedParts,
        string wireEtag,
        ChecksumSet checksums,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadataOverride = null,
        string? contentTypeOverride = null);
    IEnumerable<string> EnumerateInFlightChunkShas();
    int ReapAbandonedSessions(DateTime cutoffUtc);
}
