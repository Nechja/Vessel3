using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

internal sealed record SavedObject(string Key, string Sha256, string? VersionId);
internal sealed record RestartState(string Bucket, List<SavedObject> Objects);

internal sealed record SavedPart(int Number, string Etag, string Sha256);
internal sealed record CrashState(string Bucket, string Key, string UploadId, List<SavedPart> Parts, string ExpectedFinalSha256);

internal static class DurabilityPhases
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static async Task<int> RestartWrite(AmazonS3Client s3, string statePath)
    {
        const string bucket = "vessel3-restart-bucket";
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        var state = new RestartState(bucket, []);

        var smallBody = "small-payload"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "small.bin", InputStream = new MemoryStream(smallBody), ContentType = "application/octet-stream",
        });
        state.Objects.Add(new SavedObject("small.bin", Hex(SHA256.HashData(smallBody)), null));

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        var v1 = "version-1-payload"u8.ToArray();
        var put1 = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "ver.bin", InputStream = new MemoryStream(v1), ContentType = "application/octet-stream",
        });
        state.Objects.Add(new SavedObject("ver.bin", Hex(SHA256.HashData(v1)), put1.VersionId));

        var v2 = "version-2-payload-different"u8.ToArray();
        var put2 = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "ver.bin", InputStream = new MemoryStream(v2), ContentType = "application/octet-stream",
        });
        state.Objects.Add(new SavedObject("ver.bin", Hex(SHA256.HashData(v2)), put2.VersionId));

        const int partSize = 5 * 1024 * 1024;
        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = "multi.bin", ContentType = "application/octet-stream",
        });
        var etags = new List<PartETag>();
        var rng = new Random(42);
        var fullBytes = new MemoryStream();
        for (var i = 0; i < 3; i++)
        {
            var buf = new byte[partSize];
            rng.NextBytes(buf);
            fullBytes.Write(buf, 0, partSize);
            using var ms = new MemoryStream(buf);
            var up = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = "multi.bin", UploadId = initiate.UploadId,
                PartNumber = i + 1, PartSize = partSize, InputStream = ms,
            });
            etags.Add(new PartETag(i + 1, up.ETag));
        }
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "multi.bin", UploadId = initiate.UploadId, PartETags = etags,
        });
        state.Objects.Add(new SavedObject("multi.bin", Hex(SHA256.HashData(fullBytes.ToArray())), null));

        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts));
        Console.WriteLine($"restart-write OK: bucket={bucket} objects={state.Objects.Count} state={statePath}");
        return 0;
    }

    public static async Task<int> RestartVerify(AmazonS3Client s3, string statePath)
    {
        var state = JsonSerializer.Deserialize<RestartState>(File.ReadAllText(statePath))
                    ?? throw new InvalidOperationException("missing state file");

        try
        {
            foreach (var saved in state.Objects)
            {
                using var got = await s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = state.Bucket, Key = saved.Key, VersionId = saved.VersionId,
                });
                using var sink = new MemoryStream();
                await got.ResponseStream.CopyToAsync(sink);
                var actual = Hex(SHA256.HashData(sink.ToArray()));
                if (actual != saved.Sha256)
                    throw new InvalidOperationException($"sha mismatch on {saved.Key} (versionId={saved.VersionId ?? "<latest>"}): expected {saved.Sha256}, got {actual}");
                Console.WriteLine($"  ok: {saved.Key}{(saved.VersionId is null ? "" : "@" + saved.VersionId)} sha={actual}");
            }

            var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = state.Bucket });
            var verCount = versions.Versions?.Count(v => !v.IsDeleteMarker && v.Key == "ver.bin") ?? 0;
            if (verCount != 2)
                throw new InvalidOperationException($"expected 2 versions of ver.bin, got {verCount}");

            Console.WriteLine($"restart-verify OK: {state.Objects.Count} objects survived restart");
            return 0;
        }
        finally
        {
            await CleanupBucket(s3, state.Bucket);
            File.Delete(statePath);
        }
    }

    public static async Task<int> CrashMultipartWrite(AmazonS3Client s3, string statePath)
    {
        const string bucket = "vessel3-crash-bucket";
        const string key = "interrupted.bin";
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = key, ContentType = "application/octet-stream",
        });

        const int partSize = 5 * 1024 * 1024;
        var rng = new Random(99);
        var allBytes = new MemoryStream();
        var saved = new List<SavedPart>();
        for (var i = 0; i < 3; i++)
        {
            var buf = new byte[partSize];
            rng.NextBytes(buf);
            allBytes.Write(buf, 0, partSize);

            if (i < 2)
            {
                using var ms = new MemoryStream(buf);
                var up = await s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucket, Key = key, UploadId = initiate.UploadId,
                    PartNumber = i + 1, PartSize = partSize, InputStream = ms,
                });
                saved.Add(new SavedPart(i + 1, up.ETag, Hex(SHA256.HashData(buf))));
            }
            else
            {
                saved.Add(new SavedPart(i + 1, "<pending>", Hex(SHA256.HashData(buf))));
            }
        }

        var state = new CrashState(bucket, key, initiate.UploadId, saved, Hex(SHA256.HashData(allBytes.ToArray())));
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts));
        Console.WriteLine($"crash-multipart-write OK: uploadId={initiate.UploadId} parts uploaded=2/3");
        return 0;
    }

    public static async Task<int> CrashMultipartFinish(AmazonS3Client s3, string statePath)
    {
        var state = JsonSerializer.Deserialize<CrashState>(File.ReadAllText(statePath))
                    ?? throw new InvalidOperationException("missing state file");

        try
        {
            var listed = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = state.Bucket });
            if (listed.MultipartUploads is null || listed.MultipartUploads.All(u => u.UploadId != state.UploadId))
                throw new InvalidOperationException($"upload {state.UploadId} missing from ListMultipartUploads after crash");

            var existingParts = await s3.ListPartsAsync(new ListPartsRequest
            {
                BucketName = state.Bucket, Key = state.Key, UploadId = state.UploadId,
            });
            if (existingParts.Parts is null || existingParts.Parts.Count != 2)
                throw new InvalidOperationException($"expected 2 parts surviving crash, got {existingParts.Parts?.Count ?? 0}");

            const int partSize = 5 * 1024 * 1024;
            var rng = new Random(99);
            for (var i = 0; i < 2; i++) rng.NextBytes(new byte[partSize]);
            var part3 = new byte[partSize];
            rng.NextBytes(part3);
            using (var ms = new MemoryStream(part3))
            {
                var up = await s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = state.Bucket, Key = state.Key, UploadId = state.UploadId,
                    PartNumber = 3, PartSize = partSize, InputStream = ms,
                });
                state.Parts[2] = state.Parts[2] with { Etag = up.ETag };
            }

            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = state.Bucket, Key = state.Key, UploadId = state.UploadId,
                PartETags = state.Parts.Select(p => new PartETag(p.Number, p.Etag)).ToList(),
            });

            using var got = await s3.GetObjectAsync(state.Bucket, state.Key);
            using var sink = new MemoryStream();
            await got.ResponseStream.CopyToAsync(sink);
            var actual = Hex(SHA256.HashData(sink.ToArray()));
            if (actual != state.ExpectedFinalSha256)
                throw new InvalidOperationException($"final sha mismatch: expected {state.ExpectedFinalSha256}, got {actual}");

            Console.WriteLine($"crash-multipart-finish OK: completed {state.Key} sha={actual}");
            return 0;
        }
        finally
        {
            await CleanupBucket(s3, state.Bucket);
            File.Delete(statePath);
        }
    }

    private static async Task CleanupBucket(AmazonS3Client s3, string bucket)
    {
        try
        {
            var lv = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
            foreach (var v in lv.Versions ?? [])
                await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = v.Key, VersionId = v.VersionId });
            var ups = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
            foreach (var u in ups.MultipartUploads ?? [])
                await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = u.Key, UploadId = u.UploadId });
            await s3.DeleteBucketAsync(bucket);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  cleanup warning: {ex.Message}"); }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
