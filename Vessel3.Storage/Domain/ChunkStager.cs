using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel3.Storage;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UploadMeta))]
[JsonSerializable(typeof(StagedChunk))]
internal sealed partial class StagingJsonContext : JsonSerializerContext;

internal sealed class ChunkStager(
    ChunkStagerOptions options,
    IBucketRegistry registry,
    IBlobPool blobs,
    IDurableWrite durableWrite,
    IGcGate gate) : IChunkStager
{
    public Result<StagedSession> CreateSession(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var existsResult = registry.Exists(bucket);
        if (!existsResult.TryGetValue(out var exists, out var err))
            return err;

        if (!exists)
            return new NoSuchBucketError(bucket);

        return string.IsNullOrEmpty(key)
            ? new InvalidPathError($"{bucket}/{key}")
            : CreateSessionInternal(bucket, key, contentType, metadata);
    }

    public async Task<Result<StagedChunk>> StageChunk(
        string sessionId,
        string chunkToken,
        Stream body,
        long? declaredSize,
        DeclaredChecksums declaredChecksums,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(chunkToken))
            return new InvalidArgumentError("Chunk token cannot be empty");

        using var lease = await gate.Writing();

        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(sessionId);

        var written = await blobs.Write(body, declaredSize, declaredChecksums.ToIntent(), ct);
        if (!written.TryGetValue(out var blob, out var blobErr)) return blobErr;

        if (!ChecksumValidator.Validate(blob, declaredChecksums, body, out _, out var checksumErr))
            return checksumErr;

        var chunk = new StagedChunk(
            chunkToken,
            blob.Sha,
            blob.Md5,
            blob.Size,
            blob.Crc32,
            blob.Crc32C,
            blob.Sha1,
            DateTimeOffset.UtcNow);

        return WriteChunkFile(dir, chunk) is Result.Failure wf
            ? wf.Error
            : chunk;
    }

    public Result<IReadOnlyList<StagedChunk>> ListChunks(string sessionId)
    {
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(sessionId);

        var chunksDir = Path.Combine(dir, "chunks");
        var result = new List<StagedChunk>();
        if (!Directory.Exists(chunksDir)) return result;

        foreach (var path in Directory.EnumerateFiles(chunksDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var chunk = DeserializeChunkFile(path);
            if (chunk is not null) result.Add(chunk);
        }
        return result;
    }

    public Result<StagedSession> GetSession(string sessionId)
    {
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(sessionId);

        var meta = ReadMeta(dir);
        return meta is null
            ? new NoSuchUploadError(sessionId)
            : new StagedSession(
                sessionId,
                meta.Bucket,
                meta.Key,
                meta.ContentType,
                meta.Metadata,
                meta.CreatedAt);
    }

    public IEnumerable<StagedSession> ListSessions(string bucket)
    {
        if (!Directory.Exists(options.Root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(options.Root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var meta = ReadMeta(dir);
            if (meta is null || meta.Bucket != bucket) continue;
            yield return new StagedSession(
                Path.GetFileName(dir),
                meta.Bucket,
                meta.Key,
                meta.ContentType,
                meta.Metadata,
                meta.CreatedAt);
        }
    }

    public Result AbortSession(string sessionId)
    {
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(sessionId);
        Directory.Delete(dir, recursive: true);
        return Result.Ok;
    }

    public async Task<Result<CommitChunksOutcome>> Commit(
        string sessionId,
        IReadOnlyList<MultipartPart> orderedParts,
        string wireEtag,
        ChecksumSet checksums,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadataOverride = null,
        string? contentTypeOverride = null)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        using var lease = await gate.Writing();

        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(sessionId);

        var meta = ReadMeta(dir);
        if (meta is null) return new NoSuchUploadError(sessionId);

        var totalSize = 0L;
        foreach (var p in orderedParts) totalSize += p.Size;

        var effectiveContentType = !string.IsNullOrEmpty(contentTypeOverride) ? contentTypeOverride : meta.ContentType;
        var effectiveMetadata = metadataOverride ?? meta.Metadata;

        var put = registry.AppendPut(meta.Bucket, meta.Key, new PutRequest(
            BlobSha: "",
            Md5: wireEtag,
            Size: totalSize,
            ContentType: effectiveContentType,
            Metadata: effectiveMetadata,
            Parts: orderedParts,
            Crc32: checksums.Crc32,
            Crc32C: checksums.Crc32C,
            Sha1: checksums.Sha1));

        return put.Match<Result<CommitChunksOutcome>>(
            entry =>
            {
                Directory.Delete(dir, recursive: true);
                return new CommitChunksOutcome(entry.VersionId, totalSize);
            },
            err => err);
    }

    public IEnumerable<string> EnumerateInFlightChunkShas()
    {
        foreach (var dir in EnumerateSessionDirs())
        {
            var chunksDir = Path.Combine(dir, "chunks");
            if (!Directory.Exists(chunksDir)) continue;
            foreach (var path in Directory.EnumerateFiles(chunksDir, "*.json"))
            {
                var chunk = DeserializeChunkFile(path);
                if (chunk is not null) yield return chunk.BlobSha;
            }
        }
    }

    public int ReapAbandonedSessions(DateTime cutoffUtc)
    {
        var reaped = 0;
        foreach (var dir in EnumerateSessionDirs())
        {
            if (StartedAt(dir) > cutoffUtc) continue;
            Directory.Delete(dir, recursive: true);
            reaped++;
        }
        return reaped;
    }

    private Result<StagedSession> CreateSessionInternal(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var sessionId = Ulid.NewUlid().ToString();
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(Path.Combine(dir, "chunks"));

        var session = new StagedSession(
            sessionId,
            bucket,
            key,
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType,
            metadata,
            DateTimeOffset.UtcNow);

        var meta = new UploadMeta(
            session.Bucket,
            session.Key,
            session.ContentType,
            session.Metadata,
            session.CreatedAt);

        var metaPath = Path.Combine(dir, "meta.json");
        return durableWrite.AtomicReplace(metaPath, JsonSerializer.Serialize(meta, StagingJsonContext.Default.UploadMeta)) is Result.Failure mf
            ? mf.Error
            : session;
    }

    private Result WriteChunkFile(string sessionDir, StagedChunk chunk)
    {
        var chunksDir = Path.Combine(sessionDir, "chunks");
        Directory.CreateDirectory(chunksDir);
        var path = Path.Combine(chunksDir, ChunkFileName(chunk.Token));
        return durableWrite.AtomicReplace(path, JsonSerializer.Serialize(chunk, StagingJsonContext.Default.StagedChunk));
    }

    private static string ChunkFileName(string token) =>
        token.All(char.IsAsciiLetterOrDigit)
            ? $"{token}.json"
            : $"_hex_{Convert.ToHexStringLower(Encoding.UTF8.GetBytes(token))}.json";

    private IEnumerable<string> EnumerateSessionDirs() =>
        Directory.Exists(options.Root) ? Directory.EnumerateDirectories(options.Root) : [];

    private static DateTime StartedAt(string dir) =>
        ReadMeta(dir)?.CreatedAt.UtcDateTime ?? Directory.GetLastWriteTimeUtc(dir);

    private static UploadMeta? ReadMeta(string dir)
    {
        var path = Path.Combine(dir, "meta.json");
        return !File.Exists(path)
            ? null
            : JsonSerializer.Deserialize(File.ReadAllText(path), StagingJsonContext.Default.UploadMeta);
    }

    private static StagedChunk? DeserializeChunkFile(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), StagingJsonContext.Default.StagedChunk);

    private string SessionDir(string sessionId) => Path.Combine(options.Root, sessionId);
}
