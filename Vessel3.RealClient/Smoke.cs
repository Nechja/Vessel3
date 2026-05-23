using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

internal static class Smoke
{
    // Bare-minimum CRUD probe — for "did I break the wire?" loops before the full RealClient run.
    public static async Task<int> Run(AmazonS3Client s3)
    {
        const string bucket = "vessel3-smoke";
        const string key = "smoke.txt";
        var body = Encoding.UTF8.GetBytes("smoke\n");

        await Step("CreateBucket", () => s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }));

        await Step("PutObject", async () =>
        {
            using var ms = new MemoryStream(body);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key,
                InputStream = ms, ContentType = "text/plain",
            });
        });

        await Step("HeadObject", async () =>
        {
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            if (head.ContentLength != body.Length)
                throw new InvalidOperationException($"size {head.ContentLength} != {body.Length}");
        });

        await Step("GetObject", async () =>
        {
            using var got = await s3.GetObjectAsync(bucket, key);
            using var sink = new MemoryStream();
            await got.ResponseStream.CopyToAsync(sink);
            if (!sink.ToArray().AsSpan().SequenceEqual(body))
                throw new InvalidOperationException("body mismatch");
        });

        await Step("DeleteObject", () => s3.DeleteObjectAsync(bucket, key));
        await Step("DeleteBucket", () => s3.DeleteBucketAsync(bucket));

        Console.WriteLine();
        Console.WriteLine("SMOKE OK");
        return 0;
    }

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"==> {name,-14} ");
        try
        {
            await action();
            Console.WriteLine("ok");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
