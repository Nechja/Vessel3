using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

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

await Run("DeleteObjects",  async () =>
{
    var r = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
    {
        BucketName = bucket,
        Objects = [new KeyVersion { Key = key }, new KeyVersion { Key = copyKey }],
    });
    if (r.DeletedObjects is null || r.DeletedObjects.Count != 2)
        throw new InvalidOperationException($"expected 2 deleted, got {r.DeletedObjects?.Count ?? 0}");
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
