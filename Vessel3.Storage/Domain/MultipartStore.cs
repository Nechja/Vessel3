using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel3.Storage;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UploadMeta))]
[JsonSerializable(typeof(MultipartPart))]
internal sealed partial class MultipartJsonContext : JsonSerializerContext;

internal sealed class MultipartStore(MultipartStoreOptions options, IBucketRegistry registry, IBlobPool blobs, IDurableWrite durableWrite, IGcGate gate) : IMultipartStore
{
    public Result<CreateUploadOutcome> Create(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata) =>
        registry.Exists(bucket).Match<Result<CreateUploadOutcome>>(
            exists => !exists ? new NoSuchBucketError(bucket)
                : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
                : CreateUpload(bucket, key, contentType, metadata),
            err => err);

    public Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, ChecksumSet declaredChecksums, CancellationToken ct) =>
        UploadPart(uploadId, partNumber, body, declaredSize, DeclaredChecksums.FromSet(declaredChecksums), ct);

    public async Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, DeclaredChecksums declaredChecksums, CancellationToken ct)
    {
        if (partNumber is < 1 or > 10000)
            return new InvalidPartError($"partNumber {partNumber} out of range [1, 10000]");

        using var lease = await gate.Writing();

        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var written = await blobs.Write(body, declaredSize, declaredChecksums.ToIntent(), ct);
        if (!written.TryGetValue(out var blob, out var blobErr)) return blobErr;

        if (!ChecksumValidator.Validate(blob, declaredChecksums, body, out var toStore, out var checksumErr))
            return checksumErr;

        var part = new MultipartPart(partNumber, blob.Sha, blob.Md5, blob.Size,
            Crc32: blob.Crc32, Crc32C: blob.Crc32C, Sha1: blob.Sha1);
        return WritePartFile(dir, part) is Result.Failure wf
            ? wf.Error
            : new UploadPartOutcome(blob.Md5, blob.Sha, blob.Size, toStore);
    }

    public async Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<CompletedPart> clientParts, ChecksumAlgorithm? compositeAlgo, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        using var lease = await gate.Writing();

        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var meta = ReadMeta(dir);
        if (meta is null) return new NoSuchUploadError(uploadId);

        var stored = ReadParts(dir);

        if (clientParts.Count is 0)
            return new InvalidPartError("CompleteMultipartUpload requires at least one part");

        var ordered = new List<MultipartPart>(clientParts.Count);
        var prevNumber = 0;
        foreach (var p in clientParts)
        {
            if (p.Number <= prevNumber)
                return new InvalidPartOrderError($"part numbers must be strictly ascending; got {p.Number} after {prevNumber}");
            if (!stored.TryGetValue(p.Number, out var part))
                return new InvalidPartError($"part {p.Number} not uploaded");
            var clientEtag = p.Etag.Trim('"');
            if (!string.Equals(clientEtag, part.Md5, StringComparison.OrdinalIgnoreCase))
                return new InvalidPartError($"part {p.Number} etag mismatch: client {clientEtag}, server {part.Md5}");

            if (p.Sums is not null)
            {
                if (p.Sums.Crc32 is { } a && !string.Equals(a, part.Crc32, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} crc32 mismatch");
                if (p.Sums.Crc32C is { } b && !string.Equals(b, part.Crc32C, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} crc32c mismatch");
                if (p.Sums.Sha1 is { } c && !string.Equals(c, part.Sha1, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} sha1 mismatch");
                if (p.Sums.Sha256 is { } d && !string.Equals(d, part.BlobSha, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} sha256(checksum) mismatch");
            }

            ordered.Add(part);
            prevNumber = p.Number;
        }

        const long MinPartSize = 5 * 1024 * 1024;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            if (ordered[i].Size < MinPartSize)
                return new EntityTooSmallError(ordered[i].Number, ordered[i].Size, MinPartSize);
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

    public Result Abort(string uploadId)
    {
        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);
        Directory.Delete(dir, recursive: true);
        return Result.Ok;
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
            if (StartedAt(dir) > cutoffUtc) continue;
            Directory.Delete(dir, recursive: true);
            reaped++;
        }
        return reaped;
    }

    private static DateTime StartedAt(string uploadDir) =>
        ReadMeta(uploadDir)?.CreatedAt.UtcDateTime ?? Directory.GetLastWriteTimeUtc(uploadDir);

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

    private Result<CreateUploadOutcome> CreateUpload(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata)
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
        return durableWrite.AtomicReplace(metaPath, JsonSerializer.Serialize(meta, MultipartJsonContext.Default.UploadMeta)) is Result.Failure mf
            ? mf.Error
            : new CreateUploadOutcome(uploadId);
    }

    private Result WritePartFile(string uploadDir, MultipartPart part)
    {
        var partsDir = Path.Combine(uploadDir, "parts");
        Directory.CreateDirectory(partsDir);
        var name = part.Number.ToString("D5", CultureInfo.InvariantCulture) + ".json";
        var path = Path.Combine(partsDir, name);
        return durableWrite.AtomicReplace(path, JsonSerializer.Serialize(part, MultipartJsonContext.Default.MultipartPart));
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

    internal static string ComputeCompositeMd5(IReadOnlyList<MultipartPart> parts)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        Span<byte> partMd5 = stackalloc byte[16];
        foreach (var p in parts)
        {
            Convert.FromHexString(p.Md5, partMd5, out _, out _);
            md5.AppendData(partMd5);
        }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private string UploadDir(string uploadId) => Path.Combine(options.Root, uploadId);
}
