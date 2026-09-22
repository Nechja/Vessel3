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

internal sealed record GrowthStep(int Keys, LatencySummary ListEnd, LatencySummary ListMiss);

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

    public static async Task<LatencySummary> List(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        await SeedPrefixed(s3, opts, opts.ObjectSize > 0 ? opts.ObjectSize : 4096, ChunkKey);

        return await RunWorkload(opts, async (wid, ct) =>
        {
            var tenant = Random.Shared.Next(16);
            var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = opts.Bucket, Prefix = $"chunks/{tenant:x}/", MaxKeys = 1000,
            }, ct);
            return listed.KeyCount ?? 0;
        });
    }

    public static async Task<IReadOnlyList<GrowthStep>> ListGrowth(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        var target = Math.Max(opts.SeedKeys, 16_000);
        var size = opts.ObjectSize > 0 ? opts.ObjectSize : 1024;
        var steps = new List<GrowthStep>();
        var seeded = 0;
        for (var n = 1000; ; n = Math.Min(n * 2, target))
        {
            await SeedPrefixed(s3, opts, size, ChunkKey, seeded, n - seeded);
            seeded = n;
            var end = await RunWorkload(opts, (_, ct) => ListFirstPage(s3, opts, "chunks/f/", ct));
            var miss = await RunWorkload(opts, (_, ct) => ListFirstPage(s3, opts, "index/", ct));
            steps.Add(new GrowthStep(n, end, miss));
            if (n >= target) return steps;
        }
    }

    private static async Task<long> ListFirstPage(AmazonS3Client s3, BenchOptions opts, string prefix, CancellationToken ct)
    {
        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = opts.Bucket, Prefix = prefix, MaxKeys = 1000,
        }, ct);
        return listed.KeyCount ?? 0;
    }

    public static async Task<LatencySummary> BulkDelete(AmazonS3Client s3, BenchOptions opts)
    {
        await EnsureBucket(s3, opts);
        var keys = await SeedPrefixed(s3, opts, opts.ObjectSize > 0 ? opts.ObjectSize : 4096, ChunkKey);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<string>(keys);

        var recorder = new LatencyRecorder();
        var sw = Stopwatch.StartNew();
        async Task Worker()
        {
            while (true)
            {
                var batch = new List<KeyVersion>(1000);
                while (batch.Count < 1000 && queue.TryDequeue(out var key))
                    batch.Add(new KeyVersion { Key = key });
                if (batch.Count is 0) return;

                var t0 = Stopwatch.GetTimestamp();
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = opts.Bucket, Objects = batch, Quiet = true,
                });
                recorder.Record(Stopwatch.GetTimestamp() - t0, batch.Count);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, opts.Concurrency).Select(_ => Worker()));
        sw.Stop();
        return recorder.Summarize(sw.Elapsed);
    }

    public static Task<LatencySummary> Loki(AmazonS3Client s3, BenchOptions opts) => RunLoki(s3, opts, batchDelete: true);

    public static Task<LatencySummary> LokiSingleDelete(AmazonS3Client s3, BenchOptions opts) => RunLoki(s3, opts, batchDelete: false);

    private static async Task<LatencySummary> RunLoki(AmazonS3Client s3, BenchOptions opts, bool batchDelete)
    {
        await EnsureBucket(s3, opts);
        var size = opts.ObjectSize > 0 ? opts.ObjectSize : 1536 * 1024;
        var seeded = await SeedPrefixed(s3, opts, 4096, ChunkKey);

        var buffers = new byte[opts.Concurrency][];
        for (var i = 0; i < buffers.Length; i++)
        {
            buffers[i] = new byte[size];
            Random.Shared.NextBytes(buffers[i]);
        }
        var counters = new long[opts.Concurrency];
        var pending = new List<string>[opts.Concurrency];
        for (var i = 0; i < pending.Length; i++) pending[i] = [];

        return await RunWorkload(opts, async (wid, ct) =>
        {
            if (pending[wid].Count >= 500)
            {
                if (!batchDelete)
                {
                    var single = pending[wid][0];
                    pending[wid].RemoveAt(0);
                    await s3.DeleteObjectAsync(opts.Bucket, single, ct);
                    return 1;
                }
                var batch = pending[wid].Select(k => new KeyVersion { Key = k }).ToList();
                pending[wid].Clear();
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = opts.Bucket, Objects = batch, Quiet = true,
                }, ct);
                return batch.Count;
            }

            var roll = Random.Shared.Next(100);
            if (roll < 40)
            {
                var key = seeded[Random.Shared.Next(seeded.Length)];
                using var got = await s3.GetObjectAsync(opts.Bucket, key, ct);
                await got.ResponseStream.CopyToAsync(Stream.Null, ct);
                return got.ContentLength;
            }
            if (roll < 80)
            {
                var payload = buffers[wid];
                if (payload.Length >= 8)
                    BitConverter.TryWriteBytes(payload.AsSpan(0, 8), ++counters[wid]);
                var key = $"chunks/{Random.Shared.Next(16):x}/w{wid}/{Guid.NewGuid():N}";
                using var ms = new MemoryStream(payload);
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = opts.Bucket, Key = key, InputStream = ms, ContentType = "application/octet-stream",
                }, ct);
                pending[wid].Add(key);
                return payload.Length;
            }

            var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = opts.Bucket, Prefix = $"chunks/{Random.Shared.Next(16):x}/", MaxKeys = 1000,
            }, ct);
            return listed.KeyCount ?? 0;
        });
    }

    private static string ChunkKey(int i) => $"chunks/{i % 16:x}/{i / 1000:D5}/{i:D7}";

    private static Task<string[]> SeedPrefixed(AmazonS3Client s3, BenchOptions opts, int size, Func<int, string> keyFor) =>
        SeedPrefixed(s3, opts, size, keyFor, 0, opts.SeedKeys);

    private static async Task<string[]> SeedPrefixed(AmazonS3Client s3, BenchOptions opts, int size, Func<int, string> keyFor, int from, int count)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var keys = new string[count];
        var next = -1;

        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= keys.Length) return;
                keys[i] = keyFor(from + i);
                using var ms = new MemoryStream(payload);
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = opts.Bucket, Key = keys[i], InputStream = ms, ContentType = "application/octet-stream",
                });
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(opts.Concurrency, 8)).Select(_ => Worker()));
        return keys;
    }

    private static async Task<LatencySummary> RunPutWorkload(AmazonS3Client s3, BenchOptions opts, int size)
    {
        // One buffer per worker; stamp a per-op counter so every PUT has distinct content
        // (a shared payload would dedup to one blob and skip the create/rename/dir-fsync path).
        var buffers = new byte[opts.Concurrency][];
        for (var i = 0; i < buffers.Length; i++)
        {
            buffers[i] = new byte[size];
            Random.Shared.NextBytes(buffers[i]);
        }
        var counters = new long[opts.Concurrency];

        return await RunWorkload(opts, async (wid, ct) =>
        {
            var payload = buffers[wid];
            if (payload.Length >= 8)
                BitConverter.TryWriteBytes(payload.AsSpan(0, 8), ++counters[wid]);
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
