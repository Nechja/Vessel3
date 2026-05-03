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

    var rangeStart = partSize - 5L;
    var rangeEnd = partSize + 4L;
    using var ranged = await s3.GetObjectAsync(new GetObjectRequest
    {
        BucketName = bucket,
        Key = mpManualKey,
        ByteRange = new ByteRange(rangeStart, rangeEnd),
    });
    using var rsink = new MemoryStream();
    await ranged.ResponseStream.CopyToAsync(rsink);
    var rbytes = rsink.ToArray();
    if (rbytes.Length != 10)
        throw new InvalidOperationException($"range got {rbytes.Length} bytes, expected 10");
    var expected = new byte[10];
    Array.Copy(partBuffers[0], partSize - 5, expected, 0, 5);
    Array.Copy(partBuffers[1], 0, expected, 5, 5);
    if (!rbytes.AsSpan().SequenceEqual(expected))
        throw new InvalidOperationException("range bytes spanning part boundary mismatch");
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
            new KeyVersion { Key = mpTransferKey },
        ],
    });
    if (r.DeletedObjects is null || r.DeletedObjects.Count != 4)
        throw new InvalidOperationException($"expected 4 deleted, got {r.DeletedObjects?.Count ?? 0}");
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
