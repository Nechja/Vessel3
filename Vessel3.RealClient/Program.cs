using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

var endpoint = Environment.GetEnvironmentVariable("VESSEL3_ENDPOINT") ?? "http://127.0.0.1:9000";
var accessKey = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_KEY") ?? "AKIATEST";
var secretKey = Environment.GetEnvironmentVariable("VESSEL3_SECRET_KEY") ?? "secretkey1234567890";
var region    = Environment.GetEnvironmentVariable("VESSEL3_REGION")     ?? "us-east-1";

var config = new AmazonS3Config
{
    ServiceURL = endpoint,
    ForcePathStyle = true,
    AuthenticationRegion = region,
};

using var s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);

const string bucket = "vessel3-realclient";
const string key = "hello.txt";
const string body = "Hello, Vessel3!\n";

await Run("CreateBucket",   () => s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }));

await Run("ListBuckets",    async () =>
{
    var r = await s3.ListBucketsAsync();
    var found = r.Buckets?.Any(b => b.BucketName == bucket) ?? false;
    if (!found) throw new InvalidOperationException($"bucket '{bucket}' not present in ListBuckets");
});

await Run("PutObject",      async () =>
{
    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
    await s3.PutObjectAsync(new PutObjectRequest
    {
        BucketName = bucket,
        Key = key,
        InputStream = ms,
        ContentType = "text/plain",
    });
});

await Run("HeadObject",     async () =>
{
    var r = await s3.GetObjectMetadataAsync(bucket, key);
    if (r.ContentLength != body.Length)
        throw new InvalidOperationException($"size {r.ContentLength} != expected {body.Length}");
});

await Run("ListObjectsV2",  async () =>
{
    var r = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
    var found = r.S3Objects?.Any(o => o.Key == key) ?? false;
    if (!found) throw new InvalidOperationException($"key '{key}' not present in ListObjectsV2");
});

await Run("GetObject",      async () =>
{
    using var r = await s3.GetObjectAsync(bucket, key);
    using var sr = new StreamReader(r.ResponseStream);
    var got = await sr.ReadToEndAsync();
    if (got != body) throw new InvalidOperationException($"body mismatch: got '{got}', expected '{body}'");
});

await Run("UserMetadata",   async () =>
{
    const string metaKey = "user-meta.txt";
    using var ms = new MemoryStream(Encoding.UTF8.GetBytes("metadata test"));
    var put = new PutObjectRequest
    {
        BucketName = bucket,
        Key = metaKey,
        InputStream = ms,
        ContentType = "text/plain",
    };
    put.Metadata.Add("foo", "bar");
    put.Metadata.Add("answer", "42");
    await s3.PutObjectAsync(put);

    var head = await s3.GetObjectMetadataAsync(bucket, metaKey);
    var foo = head.Metadata["foo"];
    var answer = head.Metadata["answer"];
    if (foo != "bar") throw new InvalidOperationException($"foo: got '{foo}', expected 'bar'");
    if (answer != "42") throw new InvalidOperationException($"answer: got '{answer}', expected '42'");

    await s3.DeleteObjectAsync(bucket, metaKey);
});

const string copyKey = "hello-copy.txt";

await Run("CopyObject",     async () =>
{
    await s3.CopyObjectAsync(new CopyObjectRequest
    {
        SourceBucket = bucket, SourceKey = key,
        DestinationBucket = bucket, DestinationKey = copyKey,
    });
    var head = await s3.GetObjectMetadataAsync(bucket, copyKey);
    if (head.ContentLength != body.Length)
        throw new InvalidOperationException($"copy size {head.ContentLength} != expected {body.Length}");
});

await Run("ConditionalPut", async () =>
{
    using var ms = new MemoryStream(Encoding.UTF8.GetBytes("should fail"));
    var req = new PutObjectRequest
    {
        BucketName = bucket, Key = key,
        InputStream = ms, ContentType = "text/plain",
    };
    req.Headers["If-None-Match"] = "*";
    try
    {
        await s3.PutObjectAsync(req);
        throw new InvalidOperationException("expected 412 PreconditionFailed for If-None-Match: * on existing key");
    }
    catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 412)
    {
        // expected
    }
});

const string mpManualKey = "multipart-manual.bin";
const string mpCopyKey = "multipart-manual-copy.bin";
const string mpOutOfOrderKey = "multipart-out-of-order.bin";
const string mpBadEtagKey = "multipart-bad-etag.bin";
const string mpEmptyKey = "multipart-empty.bin";
const string mpMissingKey = "multipart-missing.bin";
const string mpDupKey = "multipart-dup.bin";
const string mpAbortKey = "multipart-abort.bin";
const string mpTransferKey = "transfer-util.bin";

await Run("MultipartManual", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpManualKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var rng = new Random(42);
    var partBuffers = new byte[3][];
    var etags = new List<PartETag>();

    for (var i = 0; i < 3; i++)
    {
        var buf = new byte[partSize];
        rng.NextBytes(buf);
        partBuffers[i] = buf;
        using var ms = new MemoryStream(buf);
        var up = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpManualKey,
            UploadId = initiate.UploadId,
            PartNumber = i + 1,
            PartSize = partSize,
            InputStream = ms,
        });
        etags.Add(new PartETag(i + 1, up.ETag));
    }

    var complete = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpManualKey,
        UploadId = initiate.UploadId,
        PartETags = etags,
    });
    if (!complete.ETag.Trim('"').EndsWith("-3", StringComparison.Ordinal))
        throw new InvalidOperationException($"completed ETag missing -3 suffix: {complete.ETag}");

    var head = await s3.GetObjectMetadataAsync(bucket, mpManualKey);
    if (head.ContentLength != partSize * 3L)
        throw new InvalidOperationException($"size {head.ContentLength} != expected {partSize * 3L}");
    if (!head.ETag.Trim('"').EndsWith("-3", StringComparison.Ordinal))
        throw new InvalidOperationException($"HEAD ETag missing -3 suffix: {head.ETag}");

    using (var got = await s3.GetObjectAsync(bucket, mpManualKey))
    using (var sink = new MemoryStream())
    {
        await got.ResponseStream.CopyToAsync(sink);
        var bytes = sink.ToArray();
        if (bytes.Length != partSize * 3)
            throw new InvalidOperationException($"got {bytes.Length} bytes, expected {partSize * 3}");
        for (var i = 0; i < 3; i++)
        {
            var slice = new byte[partSize];
            Array.Copy(bytes, i * partSize, slice, 0, partSize);
            if (!slice.AsSpan().SequenceEqual(partBuffers[i]))
                throw new InvalidOperationException($"part {i + 1} bytes mismatch");
        }
    }

    async Task CheckRange(long start, long end, byte[] expectedBytes, string label)
    {
        using var ranged = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = mpManualKey,
            ByteRange = new ByteRange(start, end),
        });
        using var rsink = new MemoryStream();
        await ranged.ResponseStream.CopyToAsync(rsink);
        var rbytes = rsink.ToArray();
        if (!rbytes.AsSpan().SequenceEqual(expectedBytes))
            throw new InvalidOperationException($"range {label} mismatch (got {rbytes.Length} bytes)");
    }

    var crossBoundary = new byte[10];
    Array.Copy(partBuffers[0], partSize - 5, crossBoundary, 0, 5);
    Array.Copy(partBuffers[1], 0, crossBoundary, 5, 5);
    await CheckRange(partSize - 5L, partSize + 4L, crossBoundary, "boundary 1->2");

    var head100 = new byte[100];
    Array.Copy(partBuffers[0], 0, head100, 0, 100);
    await CheckRange(0, 99, head100, "head 100");

    var tail100 = new byte[100];
    Array.Copy(partBuffers[2], partSize - 100, tail100, 0, 100);
    await CheckRange(partSize * 3L - 100, partSize * 3L - 1, tail100, "tail 100");

    var withinPart2 = new byte[200];
    Array.Copy(partBuffers[1], 1000, withinPart2, 0, 200);
    await CheckRange(partSize + 1000L, partSize + 1199L, withinPart2, "within part 2");
});

await Run("MultipartOutOfOrder", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpOutOfOrderKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var rng = new Random(123);
    var parts = new byte[3][];
    for (var i = 0; i < 3; i++) { parts[i] = new byte[partSize]; rng.NextBytes(parts[i]); }

    var etags = new PartETag[3];
    foreach (var i in new[] { 1, 0, 2 })
    {
        using var ms = new MemoryStream(parts[i]);
        var up = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpOutOfOrderKey,
            UploadId = initiate.UploadId,
            PartNumber = i + 1,
            PartSize = partSize,
            InputStream = ms,
        });
        etags[i] = new PartETag(i + 1, up.ETag);
    }

    await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpOutOfOrderKey,
        UploadId = initiate.UploadId,
        PartETags = [etags[0], etags[1], etags[2]],
    });

    using var got = await s3.GetObjectAsync(bucket, mpOutOfOrderKey);
    using var sink = new MemoryStream();
    await got.ResponseStream.CopyToAsync(sink);
    var bytes = sink.ToArray();
    for (var i = 0; i < 3; i++)
    {
        var slice = new byte[partSize];
        Array.Copy(bytes, i * partSize, slice, 0, partSize);
        if (!slice.AsSpan().SequenceEqual(parts[i]))
            throw new InvalidOperationException($"out-of-order part {i + 1} mismatch");
    }
});

await Run("MultipartBadEtag", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpBadEtagKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var buf = new byte[partSize];
    new Random(11).NextBytes(buf);
    using (var ms = new MemoryStream(buf))
    {
        await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpBadEtagKey,
            UploadId = initiate.UploadId,
            PartNumber = 1,
            PartSize = partSize,
            InputStream = ms,
        });
    }

    try
    {
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = mpBadEtagKey,
            UploadId = initiate.UploadId,
            PartETags = [new PartETag(1, "deadbeefdeadbeefdeadbeefdeadbeef")],
        });
        throw new InvalidOperationException("expected 400 InvalidPart for wrong ETag");
    }
    catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 400)
    {
        // expected
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpBadEtagKey,
        UploadId = initiate.UploadId,
    });
});

await Run("MultipartCopy", async () =>
{
    await s3.CopyObjectAsync(new CopyObjectRequest
    {
        SourceBucket = bucket, SourceKey = mpManualKey,
        DestinationBucket = bucket, DestinationKey = mpCopyKey,
    });

    var head = await s3.GetObjectMetadataAsync(bucket, mpCopyKey);
    if (!head.ETag.Trim('"').EndsWith("-3", StringComparison.Ordinal))
        throw new InvalidOperationException($"copy ETag missing -3 suffix: {head.ETag}");

    using var src = await s3.GetObjectAsync(bucket, mpManualKey);
    using var srcSink = new MemoryStream();
    await src.ResponseStream.CopyToAsync(srcSink);

    using var dst = await s3.GetObjectAsync(bucket, mpCopyKey);
    using var dstSink = new MemoryStream();
    await dst.ResponseStream.CopyToAsync(dstSink);

    if (!srcSink.ToArray().AsSpan().SequenceEqual(dstSink.ToArray()))
        throw new InvalidOperationException("multipart copy bytes mismatch");
});

await Run("MultipartListEtag", async () =>
{
    var r = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
    var entry = r.S3Objects?.FirstOrDefault(o => o.Key == mpManualKey);
    if (entry is null) throw new InvalidOperationException($"key {mpManualKey} missing from list");
    var etag = entry.ETag.Trim('"');
    if (!etag.EndsWith("-3", StringComparison.Ordinal))
        throw new InvalidOperationException($"list ETag missing -3 suffix: {entry.ETag}");
});

await Run("MultipartEmpty", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpEmptyKey,
        ContentType = "application/octet-stream",
    });

    try
    {
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = mpEmptyKey,
            UploadId = initiate.UploadId,
            PartETags = [],
        });
        throw new InvalidOperationException("expected 400 InvalidPart for empty Complete");
    }
    catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 400)
    {
        // expected
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpEmptyKey,
        UploadId = initiate.UploadId,
    });
});

await Run("MultipartMissingPart", async () =>
{
    const string mpMissingKey = "multipart-missing.bin";
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpMissingKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var rng = new Random(33);
    var etags = new PartETag[2];
    for (var i = 0; i < 2; i++)
    {
        var buf = new byte[partSize];
        rng.NextBytes(buf);
        using var ms = new MemoryStream(buf);
        var up = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpMissingKey,
            UploadId = initiate.UploadId,
            PartNumber = i + 1,
            PartSize = partSize,
            InputStream = ms,
        });
        etags[i] = new PartETag(i + 1, up.ETag);
    }

    try
    {
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = mpMissingKey,
            UploadId = initiate.UploadId,
            PartETags = [etags[0], etags[1], new PartETag(3, "deadbeefdeadbeefdeadbeefdeadbeef")],
        });
        throw new InvalidOperationException("expected 400 InvalidPart for missing part 3");
    }
    catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 400)
    {
        // expected
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpMissingKey,
        UploadId = initiate.UploadId,
    });
});

await Run("MultipartDupNumber", async () =>
{
    const string mpDupKey = "multipart-dup.bin";
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpDupKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var buf = new byte[partSize];
    new Random(77).NextBytes(buf);
    using (var ms = new MemoryStream(buf))
    {
        var up = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpDupKey,
            UploadId = initiate.UploadId,
            PartNumber = 1,
            PartSize = partSize,
            InputStream = ms,
        });

        try
        {
            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = mpDupKey,
                UploadId = initiate.UploadId,
                PartETags = [new PartETag(1, up.ETag), new PartETag(1, up.ETag)],
            });
            throw new InvalidOperationException("expected 400 InvalidPartOrder for duplicate part number");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 400)
        {
            // expected
        }
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpDupKey,
        UploadId = initiate.UploadId,
    });
});

await Run("MultipartAbort", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpAbortKey,
        ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var buf = new byte[partSize];
    new Random(7).NextBytes(buf);
    using (var ms = new MemoryStream(buf))
    {
        await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = mpAbortKey,
            UploadId = initiate.UploadId,
            PartNumber = 1,
            PartSize = partSize,
            InputStream = ms,
        });
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpAbortKey,
        UploadId = initiate.UploadId,
    });

    try
    {
        await s3.GetObjectMetadataAsync(bucket, mpAbortKey);
        throw new InvalidOperationException("expected 404 after abort");
    }
    catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 404)
    {
        // expected
    }
});

await Run("TransferUtility", async () =>
{
    const int size = 12 * 1024 * 1024;
    var buf = new byte[size];
    new Random(99).NextBytes(buf);
    var expectedSha = Convert.ToHexStringLower(SHA256.HashData(buf));

    using var transfer = new TransferUtility(s3);
    using (var src = new MemoryStream(buf))
    {
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = mpTransferKey,
            InputStream = src,
            PartSize = 5 * 1024 * 1024,
            ContentType = "application/octet-stream",
        });
    }

    using var got = await s3.GetObjectAsync(bucket, mpTransferKey);
    using var sink = new MemoryStream();
    await got.ResponseStream.CopyToAsync(sink);
    var bytes = sink.ToArray();
    var gotSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (gotSha != expectedSha)
        throw new InvalidOperationException($"sha mismatch: got {gotSha}, expected {expectedSha}");
});

await Run("DeleteObjects",  async () =>
{
    var r = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
    {
        BucketName = bucket,
        Objects = [
            new KeyVersion { Key = key },
            new KeyVersion { Key = copyKey },
            new KeyVersion { Key = mpManualKey },
            new KeyVersion { Key = mpCopyKey },
            new KeyVersion { Key = mpOutOfOrderKey },
            new KeyVersion { Key = mpTransferKey },
        ],
    });
    if (r.DeletedObjects is null || r.DeletedObjects.Count != 6)
        throw new InvalidOperationException($"expected 6 deleted, got {r.DeletedObjects?.Count ?? 0}");
});

await Run("DeleteBucket",   () => s3.DeleteBucketAsync(bucket));

Console.WriteLine();
Console.WriteLine("ALL GOOD");
return 0;

static async Task Run(string name, Func<Task> action)
{
    Console.Write($"==> {name,-15} ");
    try
    {
        await action();
        Console.WriteLine("ok");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is { } inner)
            Console.WriteLine($"     inner: {inner.GetType().Name}: {inner.Message}");
        throw;
    }
}
