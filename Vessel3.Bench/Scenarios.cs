using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;

namespace Vessel3.Bench;

internal sealed record BenchOptions(
    string Bucket,
    int Concurrency,
    TimeSpan Duration,
    TimeSpan Warmup,
    int ObjectSize,
    int SeedKeys);

internal static class Scenarios
{
    public static async Task<LatencySummary> PutSmall(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        return await RunPutWorkload(s3, opts, opts.ObjectSize > 0 ? opts.ObjectSize : 1024);
    }

    public static async Task<LatencySummary> PutLarge(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        return await RunPutWorkload(s3, opts, opts.ObjectSize > 0 ? opts.ObjectSize : 10 * 1024 * 1024);
    }

    public static async Task<LatencySummary> Get(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        var keys = await SeedBucket(s3, opts, opts.ObjectSize > 0 ? opts.ObjectSize : 4096);
        return await RunWorkload(opts, async (wid, ct) =>
        {
            var key = keys[Random.Shared.Next(keys.Length)];
            using var got = await s3.GetObjectAsync(opts.Bucket, key, ct);
            await got.ResponseStream.CopyToAsync(Stream.Null, ct);
            return got.ContentLength;
        });
    }

    public static async Task<LatencySummary> Multipart(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        const int partSize = 5 * 1024 * 1024;
        var partBytes = new byte[partSize];
        Random.Shared.NextBytes(partBytes);

        return await RunWorkload(opts, async (wid, ct) =>
        {
            var key = $"mp/w{wid}/{Guid.NewGuid():N}";
            var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = opts.Bucket, Key = key, ContentType = "application/octet-stream",
            }, ct);
            var etags = new List<PartETag>(3);
            for (var i = 0; i < 3; i++)
            {
                using var ms = new MemoryStream(partBytes);
                var up = await s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = opts.Bucket, Key = key, UploadId = initiate.UploadId,
                    PartNumber = i + 1, PartSize = partSize, InputStream = ms,
                }, ct);
                etags.Add(new PartETag(i + 1, up.ETag));
            }
            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = opts.Bucket, Key = key, UploadId = initiate.UploadId, PartETags = etags,
            }, ct);
            return partSize * 3L;
        });
    }

    public static async Task<LatencySummary> Mixed(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        var size = opts.ObjectSize > 0 ? opts.ObjectSize : 4096;
        var keys = await SeedBucket(s3, opts, size);
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        return await RunWorkload(opts, async (wid, ct) =>
        {
            var roll = Random.Shared.Next(100);
            if (roll < 70)
            {
                var key = keys[Random.Shared.Next(keys.Length)];
                using var got = await s3.GetObjectAsync(opts.Bucket, key, ct);
                await got.ResponseStream.CopyToAsync(Stream.Null, ct);
                return got.ContentLength;
            }
            else
            {
                var key = $"mix/w{wid}/{Guid.NewGuid():N}";
                using var ms = new MemoryStream(payload);
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = opts.Bucket, Key = key, InputStream = ms, ContentType = "application/octet-stream",
                }, ct);
                return payload.Length;
            }
        });
    }

    private static async Task<LatencySummary> RunPutWorkload(AmazonS3Client s3, BenchOptions opts, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        return await RunWorkload(opts, async (wid, ct) =>
        {
            var key = $"bench/w{wid}/{Guid.NewGuid():N}";
            using var ms = new MemoryStream(payload);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = opts.Bucket, Key = key, InputStream = ms, ContentType = "application/octet-stream",
            }, ct);
            return payload.Length;
        });
    }

    private static async Task<LatencySummary> RunWorkload(BenchOptions opts, Func<int, CancellationToken, Task<long>> op)
    {
        if (opts.Warmup > TimeSpan.Zero)
        {
            using var warmupCts = new CancellationTokenSource(opts.Warmup);
            await RunOnce(opts.Concurrency, warmupCts.Token, op);
        }

        var recorder = new LatencyRecorder();
        using var cts = new CancellationTokenSource(opts.Duration);
        var sw = Stopwatch.StartNew();
        await RunOnce(opts.Concurrency, cts.Token, async (wid, ct) =>
        {
            var t0 = Stopwatch.GetTimestamp();
            var bytes = await op(wid, ct);
            recorder.Record(Stopwatch.GetTimestamp() - t0, bytes);
            return bytes;
        });
        sw.Stop();
        return recorder.Summarize(sw.Elapsed);
    }

    private static async Task RunOnce(int concurrency, CancellationToken ct, Func<int, CancellationToken, Task<long>> op)
    {
        async Task Worker(int wid)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await op(wid, ct); }
                catch (OperationCanceledException) { return; }
                catch (AmazonS3Exception) { /* swallow transient race; counted in failures elsewhere */ }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(Worker));
    }

    public static async Task EnsureBucket(AmazonS3Client s3, BenchOptions opts)
    {
        try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = opts.Bucket }); }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 409 || ex.ErrorCode == "BucketAlreadyOwnedByYou") { }
    }

    private static async Task<string[]> SeedBucket(AmazonS3Client s3, BenchOptions opts, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var keys = new string[opts.SeedKeys];
        for (var i = 0; i < opts.SeedKeys; i++)
        {
            keys[i] = $"seed/{i:D5}";
            using var ms = new MemoryStream(payload);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = opts.Bucket, Key = keys[i], InputStream = ms, ContentType = "application/octet-stream",
            });
        }
        return keys;
    }
}
