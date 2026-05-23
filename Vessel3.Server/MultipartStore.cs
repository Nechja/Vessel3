using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record MultipartStoreOptions(string Root);

internal sealed record CreateUploadOutcome(string UploadId);
internal sealed record UploadPartOutcome(string Etag, string BlobSha, long Size, ChecksumSet Checksums);
internal sealed record CompleteUploadOutcome(string Etag, string VersionId, long Size, ChecksumSet Checksums);
internal sealed record InProgressUpload(string UploadId, string Bucket, string Key, DateTimeOffset Initiated);
internal sealed record ListedPart(int Number, string Etag, long Size, DateTimeOffset LastModified);

internal sealed record CompletedPartChecksums(string? Crc32, string? Crc32C, string? Sha1, string? Sha256);

internal sealed record UploadMeta(
    string Bucket,
    string Key,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UploadMeta))]
[JsonSerializable(typeof(MultipartPart))]
internal sealed partial class MultipartJsonContext : JsonSerializerContext;

internal interface IMultipartStore
{
    Result<CreateUploadOutcome> Create(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata);
    Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, ChecksumSet declaredChecksums, CancellationToken ct);
    Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<(int Number, string Etag, CompletedPartChecksums? Sums)> clientParts, ChecksumAlgorithm? compositeAlgo, CancellationToken ct);
    Result<bool> Abort(string uploadId);
    IEnumerable<InProgressUpload> ListUploads(string bucket);
    Result<IReadOnlyList<ListedPart>> ListParts(string uploadId);
    IEnumerable<string> EnumerateInFlightPartShas();
    int ReapAbandonedUploads(DateTime cutoffUtc);
}

internal sealed class MultipartStore(MultipartStoreOptions options, IBucketRegistry registry, IBlobPool blobs) : IMultipartStore
{
    public Result<CreateUploadOutcome> Create(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata) =>
        registry.Exists(bucket).Match<Result<CreateUploadOutcome>>(
            exists => !exists ? new NotFoundError(bucket)
                : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
                : CreateUpload(bucket, key, contentType, metadata),
            err => err);

    public async Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, ChecksumSet declaredChecksums, CancellationToken ct)
    {
        if (partNumber is < 1 or > 10000)
            return new InvalidPartError($"partNumber {partNumber} out of range [1, 10000]");

        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var written = await blobs.Write(body, declaredSize, ct);
        if (written is Result<StoredBlob>.Failure f) return f.Error;
        var blob = ((Result<StoredBlob>.Success)written).Value;

        if (declaredChecksums.Crc32 is { } c32 && !string.Equals(c32, blob.Crc32, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"part {partNumber} crc32 mismatch");
        if (declaredChecksums.Crc32C is { } c32c && !string.Equals(c32c, blob.Crc32C, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"part {partNumber} crc32c mismatch");
        if (declaredChecksums.Sha1 is { } s1 && !string.Equals(s1, blob.Sha1, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"part {partNumber} sha1 mismatch");
        if (declaredChecksums.Sha256 is { } s256 && !string.Equals(s256, blob.Sha, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"part {partNumber} sha256(checksum) mismatch");

        var part = new MultipartPart(partNumber, blob.Sha, blob.Md5, blob.Size,
            Crc32: blob.Crc32, Crc32C: blob.Crc32C, Sha1: blob.Sha1);
        WritePartFile(dir, part);
        var sums = new ChecksumSet(blob.Crc32, blob.Crc32C, blob.Sha1, blob.Sha);
        return new UploadPartOutcome(blob.Md5, blob.Sha, blob.Size, sums);
    }

    public async Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<(int Number, string Etag, CompletedPartChecksums? Sums)> clientParts, ChecksumAlgorithm? compositeAlgo, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var meta = ReadMeta(dir);
        if (meta is null) return new NoSuchUploadError(uploadId);

        var stored = ReadParts(dir);

        if (clientParts.Count is 0)
            return new InvalidPartError("CompleteMultipartUpload requires at least one part");

        var ordered = new List<MultipartPart>(clientParts.Count);
        var prevNumber = 0;
        foreach (var (n, etag, sums) in clientParts)
        {
            if (n <= prevNumber)
                return new InvalidPartOrderError($"part numbers must be strictly ascending; got {n} after {prevNumber}");
            if (!stored.TryGetValue(n, out var part))
                return new InvalidPartError($"part {n} not uploaded");
            var clientEtag = etag.Trim('"');
            if (!string.Equals(clientEtag, part.Md5, StringComparison.OrdinalIgnoreCase))
                return new InvalidPartError($"part {n} etag mismatch: client {clientEtag}, server {part.Md5}");

            if (sums is not null)
            {
                if (sums.Crc32 is { } a && !string.Equals(a, part.Crc32, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {n} crc32 mismatch");
                if (sums.Crc32C is { } b && !string.Equals(b, part.Crc32C, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {n} crc32c mismatch");
                if (sums.Sha1 is { } c && !string.Equals(c, part.Sha1, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {n} sha1 mismatch");
                if (sums.Sha256 is { } d && !string.Equals(d, part.BlobSha, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {n} sha256(checksum) mismatch");
            }

            ordered.Add(part);
            prevNumber = n;
        }

        var composite = ComputeCompositeMd5(ordered);
        var totalSize = 0L;
        foreach (var p in ordered) totalSize += p.Size;

        var objectSums = ChecksumSet.Empty;
        if (compositeAlgo is { } algo)
        {
            var partHexes = algo switch
            {
                ChecksumAlgorithm.Crc32 => ordered.Select(p => p.Crc32 ?? ""),
                ChecksumAlgorithm.Crc32C => ordered.Select(p => p.Crc32C ?? ""),
                ChecksumAlgorithm.Sha1 => ordered.Select(p => p.Sha1 ?? ""),
                ChecksumAlgorithm.Sha256 => ordered.Select(p => p.BlobSha ?? ""),
                _ => [],
            };
            if (partHexes.Any(static h => string.IsNullOrEmpty(h)))
                return new BadDigestError($"composite {algo}: a part is missing its per-part value");
            var hex = ChecksumAlgorithms.Composite(algo, partHexes);
            objectSums = algo switch
            {
                ChecksumAlgorithm.Crc32 => new ChecksumSet(hex, null, null, null),
                ChecksumAlgorithm.Crc32C => new ChecksumSet(null, hex, null, null),
                ChecksumAlgorithm.Sha1 => new ChecksumSet(null, null, hex, null),
                ChecksumAlgorithm.Sha256 => new ChecksumSet(null, null, null, hex),
                _ => ChecksumSet.Empty,
            };
        }

        var put = registry.AppendPut(meta.Bucket, meta.Key, new PutRequest(
            BlobSha: "",
            Md5: composite,
            Size: totalSize,
            ContentType: meta.ContentType,
            Metadata: meta.Metadata,
            Parts: ordered,
            Crc32: objectSums.Crc32,
            Crc32C: objectSums.Crc32C,
            Sha1: objectSums.Sha1));

        return put.Match<Result<CompleteUploadOutcome>>(
            entry =>
            {
                Directory.Delete(dir, recursive: true);
                return new CompleteUploadOutcome($"{composite}-{ordered.Count}", entry.VersionId, totalSize, objectSums);
            },
            err => err);
    }

    public Result<bool> Abort(string uploadId)
    {
        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);
        Directory.Delete(dir, recursive: true);
        return true;
    }

    public IEnumerable<InProgressUpload> ListUploads(string bucket)
    {
        if (!Directory.Exists(options.Root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(options.Root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var meta = ReadMeta(dir);
            if (meta is null || meta.Bucket != bucket) continue;
            yield return new InProgressUpload(Path.GetFileName(dir), meta.Bucket, meta.Key, meta.CreatedAt);
        }
    }

    public IEnumerable<string> EnumerateInFlightPartShas()
    {
        foreach (var dir in EnumerateUploadDirs())
        {
            var partsDir = Path.Combine(dir, "parts");
            if (!Directory.Exists(partsDir)) continue;
            foreach (var path in Directory.EnumerateFiles(partsDir, "*.json"))
            {
                var part = DeserializePartFile(path);
                if (part is not null) yield return part.BlobSha;
            }
        }
    }

    public int ReapAbandonedUploads(DateTime cutoffUtc)
    {
        var reaped = 0;
        foreach (var dir in EnumerateUploadDirs())
        {
            var meta = ReadMeta(dir);
            if (meta is null) continue;
            if (meta.CreatedAt.UtcDateTime > cutoffUtc) continue;
            Directory.Delete(dir, recursive: true);
            reaped++;
        }
        return reaped;
    }

    private IEnumerable<string> EnumerateUploadDirs() =>
        Directory.Exists(options.Root) ? Directory.EnumerateDirectories(options.Root) : [];

    public Result<IReadOnlyList<ListedPart>> ListParts(string uploadId)
    {
        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var partsDir = Path.Combine(dir, "parts");
        var result = new List<ListedPart>();
        if (!Directory.Exists(partsDir)) return result;

        foreach (var path in Directory.EnumerateFiles(partsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var part = DeserializePartFile(path);
            if (part is null) continue;
            result.Add(new ListedPart(part.Number, part.Md5, part.Size, File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }

    private CreateUploadOutcome CreateUpload(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var uploadId = Ulid.NewUlid().ToString();
        var dir = UploadDir(uploadId);
        Directory.CreateDirectory(Path.Combine(dir, "parts"));

        var meta = new UploadMeta(
            bucket, key,
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType,
            metadata,
            DateTimeOffset.UtcNow);

        var metaPath = Path.Combine(dir, "meta.json");
        var tmp = metaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, MultipartJsonContext.Default.UploadMeta));
        File.Move(tmp, metaPath, overwrite: true);

        return new CreateUploadOutcome(uploadId);
    }

    private static void WritePartFile(string uploadDir, MultipartPart part)
    {
        var partsDir = Path.Combine(uploadDir, "parts");
        Directory.CreateDirectory(partsDir);
        var name = part.Number.ToString("D5", CultureInfo.InvariantCulture) + ".json";
        var path = Path.Combine(partsDir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(part, MultipartJsonContext.Default.MultipartPart));
        File.Move(tmp, path, overwrite: true);
    }

    private static UploadMeta? ReadMeta(string uploadDir)
    {
        var path = Path.Combine(uploadDir, "meta.json");
        return !File.Exists(path)
            ? null
            : JsonSerializer.Deserialize(File.ReadAllText(path), MultipartJsonContext.Default.UploadMeta);
    }

    private static Dictionary<int, MultipartPart> ReadParts(string uploadDir)
    {
        var partsDir = Path.Combine(uploadDir, "parts");
        var map = new Dictionary<int, MultipartPart>();
        if (!Directory.Exists(partsDir)) return map;
        foreach (var path in Directory.EnumerateFiles(partsDir, "*.json"))
        {
            var part = DeserializePartFile(path);
            if (part is not null) map[part.Number] = part;
        }
        return map;
    }

    private static MultipartPart? DeserializePartFile(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), MultipartJsonContext.Default.MultipartPart);

    private static string ComputeCompositeMd5(IReadOnlyList<MultipartPart> parts)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var p in parts)
            md5.AppendData(Convert.FromHexString(p.Md5));
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private string UploadDir(string uploadId) => Path.Combine(options.Root, uploadId);
}
