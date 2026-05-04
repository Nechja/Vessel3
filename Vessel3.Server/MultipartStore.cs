using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record MultipartStoreOptions(string Root);

internal sealed record CreateUploadOutcome(string UploadId);
internal sealed record UploadPartOutcome(string Etag, string BlobSha, long Size);
internal sealed record CompleteUploadOutcome(string Etag, string VersionId, long Size);
internal sealed record InProgressUpload(string UploadId, string Bucket, string Key, DateTimeOffset Initiated);
internal sealed record ListedPart(int Number, string Etag, long Size, DateTimeOffset LastModified);

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
    Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, CancellationToken ct);
    Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<(int Number, string Etag)> clientParts, CancellationToken ct);
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

    public async Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, CancellationToken ct)
    {
        if (partNumber is < 1 or > 10000)
            return new InvalidPartError($"partNumber {partNumber} out of range [1, 10000]");

        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var written = await blobs.Write(body, declaredSize, ct);
        if (written is Result<StoredBlob>.Failure f) return f.Error;
        var blob = ((Result<StoredBlob>.Success)written).Value;

        var part = new MultipartPart(partNumber, blob.Sha, blob.Md5, blob.Size);
        WritePartFile(dir, part);
        return new UploadPartOutcome(blob.Md5, blob.Sha, blob.Size);
    }

    public async Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<(int Number, string Etag)> clientParts, CancellationToken ct)
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
        foreach (var (n, etag) in clientParts)
        {
            if (n <= prevNumber)
                return new InvalidPartOrderError($"part numbers must be strictly ascending; got {n} after {prevNumber}");
            if (!stored.TryGetValue(n, out var part))
                return new InvalidPartError($"part {n} not uploaded");
            var clientEtag = etag.Trim('"');
            if (!string.Equals(clientEtag, part.Md5, StringComparison.OrdinalIgnoreCase))
                return new InvalidPartError($"part {n} etag mismatch: client {clientEtag}, server {part.Md5}");
            ordered.Add(part);
            prevNumber = n;
        }

        var composite = ComputeCompositeMd5(ordered);
        var totalSize = 0L;
        foreach (var p in ordered) totalSize += p.Size;

        var put = registry.AppendPut(meta.Bucket, meta.Key, new PutRequest(
            BlobSha: "",
            Md5: composite,
            Size: totalSize,
            ContentType: meta.ContentType,
            Metadata: meta.Metadata,
            Parts: ordered));

        return put.Match<Result<CompleteUploadOutcome>>(
            entry =>
            {
                Directory.Delete(dir, recursive: true);
                return new CompleteUploadOutcome($"{composite}-{ordered.Count}", entry.VersionId, totalSize);
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
        if (!Directory.Exists(options.Root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(options.Root))
        {
            var partsDir = Path.Combine(dir, "parts");
            if (!Directory.Exists(partsDir)) continue;
            foreach (var path in Directory.EnumerateFiles(partsDir, "*.json"))
            {
                var part = JsonSerializer.Deserialize(File.ReadAllText(path), MultipartJsonContext.Default.MultipartPart);
                if (part is not null) yield return part.BlobSha;
            }
        }
    }

    public int ReapAbandonedUploads(DateTime cutoffUtc)
    {
        if (!Directory.Exists(options.Root)) return 0;
        var reaped = 0;
        foreach (var dir in Directory.EnumerateDirectories(options.Root))
        {
            var meta = ReadMeta(dir);
            if (meta is null) continue;
            if (meta.CreatedAt.UtcDateTime > cutoffUtc) continue;
            Directory.Delete(dir, recursive: true);
            reaped++;
        }
        return reaped;
    }

    public Result<IReadOnlyList<ListedPart>> ListParts(string uploadId)
    {
        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) return new NoSuchUploadError(uploadId);

        var partsDir = Path.Combine(dir, "parts");
        var result = new List<ListedPart>();
        if (!Directory.Exists(partsDir)) return result;

        foreach (var path in Directory.EnumerateFiles(partsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var part = JsonSerializer.Deserialize(File.ReadAllText(path), MultipartJsonContext.Default.MultipartPart);
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
            var part = JsonSerializer.Deserialize(File.ReadAllText(path), MultipartJsonContext.Default.MultipartPart);
            if (part is not null) map[part.Number] = part;
        }
        return map;
    }

    private static string ComputeCompositeMd5(IReadOnlyList<MultipartPart> parts)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var p in parts)
            md5.AppendData(Convert.FromHexString(p.Md5));
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private string UploadDir(string uploadId) => Path.Combine(options.Root, uploadId);
}
