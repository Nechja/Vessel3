using System.Globalization;
using System.Security.Cryptography;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed class S3MultipartStore(IChunkStager stager) : IMultipartStore
{
    private const long MinPartSize = 5 * 1024 * 1024;

    public Result<CreateUploadOutcome> Create(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata) =>
        stager.CreateSession(bucket, key, contentType, metadata).Match<Result<CreateUploadOutcome>>(
            session => new CreateUploadOutcome(session.SessionId),
            err => err);

    public async Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, DeclaredChecksums declaredChecksums, CancellationToken ct)
    {
        if (partNumber is < 1 or > 10000)
            return new InvalidPartError($"partNumber {partNumber} out of range [1, 10000]");

        var chunkToken = FormatPartToken(partNumber);
        var result = await stager.StageChunk(uploadId, chunkToken, body, declaredSize, declaredChecksums, ct);

        return result.Match<Result<UploadPartOutcome>>(
            chunk =>
            {
                var toStore = new ChecksumSet(
                    declaredChecksums.Crc32.HasExpectation ? chunk.Crc32 : null,
                    declaredChecksums.Crc32C.HasExpectation ? chunk.Crc32C : null,
                    declaredChecksums.Sha1.HasExpectation ? chunk.Sha1 : null,
                    declaredChecksums.Sha256.HasExpectation ? chunk.BlobSha : null);
                return new UploadPartOutcome(chunk.Md5, chunk.BlobSha, chunk.Size, toStore);
            },
            err => err);
    }

    public async Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<CompletedPart> clientParts, ChecksumAlgorithm? compositeAlgo, CancellationToken ct)
    {
        if (clientParts.Count is 0)
            return new InvalidPartError("CompleteMultipartUpload requires at least one part");

        if (!stager.ListChunks(uploadId).TryGetValue(out var stagedChunks, out var listErr))
            return listErr;

        var stored = stagedChunks.ToDictionary(c => ParsePartNumber(c.Token), c => c);

        var ordered = new List<MultipartPart>(clientParts.Count);
        var prevNumber = 0;
        foreach (var p in clientParts)
        {
            if (p.Number <= prevNumber)
                return new InvalidPartOrderError($"part numbers must be strictly ascending; got {p.Number} after {prevNumber}");
            if (!stored.TryGetValue(p.Number, out var chunk))
                return new InvalidPartError($"part {p.Number} not uploaded");

            var clientEtag = p.Etag.Trim('"');
            if (!string.Equals(clientEtag, chunk.Md5, StringComparison.OrdinalIgnoreCase))
                return new InvalidPartError($"part {p.Number} etag mismatch: client {clientEtag}, server {chunk.Md5}");

            if (p.Sums is not null)
            {
                if (p.Sums.Crc32 is { } a && !string.Equals(a, chunk.Crc32, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} crc32 mismatch");
                if (p.Sums.Crc32C is { } b && !string.Equals(b, chunk.Crc32C, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} crc32c mismatch");
                if (p.Sums.Sha1 is { } c && !string.Equals(c, chunk.Sha1, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} sha1 mismatch");
                if (p.Sums.Sha256 is { } d && !string.Equals(d, chunk.BlobSha, StringComparison.OrdinalIgnoreCase))
                    return new BadDigestError($"part {p.Number} sha256(checksum) mismatch");
            }

            ordered.Add(new MultipartPart(p.Number, chunk.BlobSha, chunk.Md5, chunk.Size, chunk.Crc32, chunk.Crc32C, chunk.Sha1));
            prevNumber = p.Number;
        }

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            if (ordered[i].Size < MinPartSize)
                return new EntityTooSmallError(ordered[i].Number, ordered[i].Size, MinPartSize);
        }

        var composite = ComputeCompositeMd5(ordered);
        var objectSums = ComputeCompositeChecksums(compositeAlgo, ordered, out var sumsErr);
        if (sumsErr is not null) return sumsErr;

        var wireEtag = $"{composite}-{ordered.Count}";
        var commitResult = await stager.Commit(uploadId, ordered, composite, objectSums, ct);

        return commitResult.Match<Result<CompleteUploadOutcome>>(
            outcome => new CompleteUploadOutcome(wireEtag, outcome.VersionId, outcome.TotalSize, objectSums),
            err => err);
    }

    public Result Abort(string uploadId) => stager.AbortSession(uploadId);

    public IEnumerable<InProgressUpload> ListUploads(string bucket) =>
        stager.ListSessions(bucket).Select(s => new InProgressUpload(s.SessionId, s.Bucket, s.Key, s.CreatedAt));

    public Result<IReadOnlyList<ListedPart>> ListParts(string uploadId) =>
        stager.ListChunks(uploadId).Match<Result<IReadOnlyList<ListedPart>>>(
            chunks => chunks
                .Select(c => new ListedPart(
                    ParsePartNumber(c.Token),
                    c.Md5,
                    c.Size,
                    c.StagedAt == default ? DateTimeOffset.UtcNow : c.StagedAt))
                .OrderBy(p => p.Number)
                .ToList(),
            err => err);

    public IEnumerable<string> EnumerateInFlightPartShas() => stager.EnumerateInFlightChunkShas();

    public int ReapAbandonedUploads(DateTime cutoffUtc) => stager.ReapAbandonedSessions(cutoffUtc);

    private static string FormatPartToken(int partNumber) =>
        partNumber.ToString("D5", CultureInfo.InvariantCulture);

    private static int ParsePartNumber(string token) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) ? num : 0;

    private static ChecksumSet ComputeCompositeChecksums(ChecksumAlgorithm? compositeAlgo, List<MultipartPart> ordered, out Error? error)
    {
        error = null;
        if (compositeAlgo is not { } algo) return ChecksumSet.Empty;

        var partHexes = algo switch
        {
            ChecksumAlgorithm.Crc32 => ordered.Select(p => p.Crc32 ?? ""),
            ChecksumAlgorithm.Crc32C => ordered.Select(p => p.Crc32C ?? ""),
            ChecksumAlgorithm.Sha1 => ordered.Select(p => p.Sha1 ?? ""),
            ChecksumAlgorithm.Sha256 => ordered.Select(p => p.BlobSha ?? ""),
            _ => [],
        };

        if (partHexes.Any(static h => string.IsNullOrEmpty(h)))
        {
            error = new BadDigestError($"composite {algo}: a part is missing its per-part value");
            return ChecksumSet.Empty;
        }

        var hex = ChecksumAlgorithms.Composite(algo, partHexes);
        return algo switch
        {
            ChecksumAlgorithm.Crc32 => new ChecksumSet(hex, null, null, null),
            ChecksumAlgorithm.Crc32C => new ChecksumSet(null, hex, null, null),
            ChecksumAlgorithm.Sha1 => new ChecksumSet(null, null, hex, null),
            ChecksumAlgorithm.Sha256 => new ChecksumSet(null, null, null, hex),
            _ => ChecksumSet.Empty,
        };
    }

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
}
