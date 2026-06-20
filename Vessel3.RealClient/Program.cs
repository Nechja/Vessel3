using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

var endpoint = Environment.GetEnvironmentVariable("VESSEL3_ENDPOINT") ?? "http://127.0.0.1:9000";
var accessKey = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_KEY") ?? "AKIATEST";
var secretKey = Environment.GetEnvironmentVariable("VESSEL3_SECRET_KEY") ?? "secretkey1234567890";
var region    = Environment.GetEnvironmentVariable("VESSEL3_REGION")     ?? "us-east-1";

#if AWSSDK_V3
Amazon.AWSConfigsS3.UseSignatureVersion4 = true;
#endif
var config = new AmazonS3Config
{
    ServiceURL = endpoint,
    ForcePathStyle = true,
    AuthenticationRegion = region,
    UseHttp = endpoint.StartsWith("http://", StringComparison.Ordinal),
};

using var s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);

if (args.Length > 0)
{
    var statePath = Environment.GetEnvironmentVariable("VESSEL3_PROBE_STATE") ?? "/tmp/vessel3-probe-state.json";
    switch (args[0])
    {
        case "restart-write": return await DurabilityTester.RestartWrite(s3, statePath);
        case "restart-verify": return await DurabilityTester.RestartVerify(s3, statePath);
        case "crash-multipart-write": return await DurabilityTester.CrashMultipartWrite(s3, statePath);
        case "crash-multipart-finish": return await DurabilityTester.CrashMultipartFinish(s3, statePath);
        case "anon": return await AnonScenarios.RunAll(
            Environment.GetEnvironmentVariable("VESSEL3_ANON_ENDPOINT") ?? "http://127.0.0.1:9101");
        case "smoke": return await Smoke.Run(s3);
        default:
            Console.Error.WriteLine($"unknown phase: {args[0]}");
            return 2;
    }
}

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

await Run("GetBucketLocation", async () =>
{
    var r = await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
    if (r.Location is null)
        throw new InvalidOperationException("Location was null; server did not return a LocationConstraint element");
    var loc = r.Location.Value;
    if (loc is not "" and not "us-east-1")
        throw new InvalidOperationException($"unexpected location '{loc}', expected '' or 'us-east-1'");
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

await Run("ListObjectsV1", async () =>
{
    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("page-2")))
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "list-v1-extra.txt",
            InputStream = ms, ContentType = "text/plain",
        });
    }

    var r = await s3.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket });
    if (r.S3Objects is null || r.S3Objects.Count is 0)
        throw new InvalidOperationException("ListObjects v1 returned no objects");
    var found = r.S3Objects.Any(o => o.Key == key);
    if (!found) throw new InvalidOperationException($"key '{key}' not present in ListObjects v1");
    if (r.Name != bucket)
        throw new InvalidOperationException($"v1 bucket name mismatch: got '{r.Name}', expected '{bucket}'");

    var paged = await s3.ListObjectsAsync(new ListObjectsRequest
    {
        BucketName = bucket,
        MaxKeys = 1,
    });
    if (paged.IsTruncated != true)
        throw new InvalidOperationException("v1 paged response not truncated with max-keys=1");
    if (string.IsNullOrEmpty(paged.NextMarker))
        throw new InvalidOperationException("v1 paged response missing NextMarker");

    var nextPage = await s3.ListObjectsAsync(new ListObjectsRequest
    {
        BucketName = bucket,
        MaxKeys = 1,
        Marker = paged.NextMarker,
    });
    if (nextPage.S3Objects is null || nextPage.S3Objects.Count is 0)
        throw new InvalidOperationException("v1 followup page empty");
    if (nextPage.S3Objects[0].Key == paged.S3Objects[0].Key)
        throw new InvalidOperationException("v1 followup returned same key as first page");

    await s3.DeleteObjectAsync(bucket, "list-v1-extra.txt");
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

await Run("ListObjectVersions", async () =>
{
    const string vbucket = "vessel3-realclient-versions";
    const string vkey = "lv.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("a")))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = vbucket, Key = vkey, InputStream = ms, ContentType = "text/plain" });
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("b")))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = vbucket, Key = vkey, InputStream = ms, ContentType = "text/plain" });
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = vbucket, Key = vkey });

        var lv = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = vbucket });
        var entries = lv.Versions ?? [];
        var versionCount = entries.Count(v => v.IsDeleteMarker != true);
        var markerCount = entries.Count(v => v.IsDeleteMarker == true);
        if (versionCount != 2)
            throw new InvalidOperationException($"expected 2 versions, got {versionCount}");
        if (markerCount != 1)
            throw new InvalidOperationException($"expected 1 delete marker, got {markerCount}");

        var latestMarker = entries.FirstOrDefault(v => v.IsDeleteMarker == true && v.IsLatest == true);
        if (latestMarker is null)
            throw new InvalidOperationException("no delete marker reported as IsLatest");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, vbucket);
    }
});

await Run("DeleteVersionId", async () =>
{
    const string vbucket = "vessel3-realclient-vdid";
    const string vkey = "v.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        string v1Id, v2Id;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v1")))
        {
            var p1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = vbucket, Key = vkey, InputStream = ms, ContentType = "text/plain" });
            v1Id = p1.VersionId;
        }
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v2")))
        {
            var p2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = vbucket, Key = vkey, InputStream = ms, ContentType = "text/plain" });
            v2Id = p2.VersionId;
        }

        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = vbucket, Key = vkey, VersionId = v1Id });

        try
        {
            await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = vbucket, Key = vkey, VersionId = v1Id });
            throw new InvalidOperationException("v1 should be hard-deleted");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 404)
        {
        }

        using var got = await s3.GetObjectAsync(vbucket, vkey);
        using var sr = new StreamReader(got.ResponseStream);
        if (await sr.ReadToEndAsync() != "v2")
            throw new InvalidOperationException("latest GET should still return v2");

        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = vbucket, Key = vkey, VersionId = v2Id });
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, vbucket);
    }
});

await Run("VersionedDelete", async () =>
{
    const string vbucket = "vessel3-realclient-vd";
    const string vkey = "deleted.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = vbucket, Key = vkey, InputStream = ms, ContentType = "text/plain",
        });
        var v1 = put.VersionId;
        if (string.IsNullOrEmpty(v1)) throw new InvalidOperationException("PUT missing version id");

        var del = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = vbucket, Key = vkey });
        if (del.DeleteMarker is not "true")
            throw new InvalidOperationException($"DELETE on versioned bucket should set x-amz-delete-marker: true (got '{del.DeleteMarker}')");
        if (string.IsNullOrEmpty(del.VersionId))
            throw new InvalidOperationException("DELETE response missing x-amz-version-id");
        if (del.VersionId == v1)
            throw new InvalidOperationException("delete marker reused PUT version id");

        try
        {
            await s3.GetObjectMetadataAsync(vbucket, vkey);
            throw new InvalidOperationException("GET after delete-marker should 404");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 404)
        {
        }

        using var got = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = vbucket, Key = vkey, VersionId = v1,
        });
        using var sr = new StreamReader(got.ResponseStream);
        if (await sr.ReadToEndAsync() != "payload")
            throw new InvalidOperationException("v1 GET via versionId should still work after delete marker");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, vbucket);
    }
});

await Run("VersionedPut", async () =>
{
    const string vbucket = "vessel3-realclient-versioned";
    const string vkey = "versioned.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        using (var ms1 = new MemoryStream(Encoding.UTF8.GetBytes("v1-body")))
        {
            var p1 = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = vbucket, Key = vkey, InputStream = ms1, ContentType = "text/plain",
            });
            if (string.IsNullOrEmpty(p1.VersionId))
                throw new InvalidOperationException("first PUT missing x-amz-version-id");
            var v1Id = p1.VersionId;

            using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("v2-body"));
            var p2 = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = vbucket, Key = vkey, InputStream = ms2, ContentType = "text/plain",
            });
            if (p2.VersionId == v1Id)
                throw new InvalidOperationException("second PUT returned same VersionId as first");

            using var latest = await s3.GetObjectAsync(vbucket, vkey);
            using var latestSr = new StreamReader(latest.ResponseStream);
            var latestBody = await latestSr.ReadToEndAsync();
            if (latestBody != "v2-body")
                throw new InvalidOperationException($"latest GET body '{latestBody}' != 'v2-body'");

            using var oldVersion = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = vbucket, Key = vkey, VersionId = v1Id,
            });
            using var oldSr = new StreamReader(oldVersion.ResponseStream);
            var oldBody = await oldSr.ReadToEndAsync();
            if (oldBody != "v1-body")
                throw new InvalidOperationException($"versioned GET v1 body '{oldBody}' != 'v1-body'");
        }
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, vbucket);
    }
});

async Task<(int blobsDeleted, int uploadsReaped)> RunGc(int blobAgeSec, int uploadAgeSec)
{
    var presign = new GetPreSignedUrlRequest
    {
        BucketName = "_admin",
        Key = "gc",
        Verb = HttpVerb.PUT,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = Protocol.HTTP,
    };
    presign.Parameters.Add("blob-age", blobAgeSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
    presign.Parameters.Add("upload-age", uploadAgeSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var url = await s3.GetPreSignedURLAsync(presign);
    using var http = new HttpClient();
    var resp = await http.PutAsync(url, null);
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"GC failed: {(int)resp.StatusCode} {resp.ReasonPhrase} url={url}");
    var json = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    return (doc.RootElement.GetProperty("BlobsDeleted").GetInt32(),
            doc.RootElement.GetProperty("UploadsReaped").GetInt32());
}

await Run("ConcurrencyStress", async () =>
{
    const string sbucket = "vessel3-realclient-stress";
    const int writerTasks = 16;
    const int readerTasks = 8;
    const int writesPerTask = 100;
    const int gcInterval = 25;
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = sbucket });
    try
    {
        var writtenKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        Task Writer(int wid) => Task.Run(async () =>
        {
            var rng = new Random(wid * 1000);
            for (var i = 0; i < writesPerTask && !cts.IsCancellationRequested; i++)
            {
                try
                {
                    var k = $"w{wid}/k{i}";
                    var payload = new byte[256 + rng.Next(2048)];
                    rng.NextBytes(payload);
                    using var ms = new MemoryStream(payload);
                    await s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = sbucket, Key = k, InputStream = ms, ContentType = "application/octet-stream",
                    }, cts.Token);
                    writtenKeys[k] = 0;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { failures.Add($"writer{wid}#{i}: {ex.GetType().Name}: {ex.Message}"); }
            }
        });

        Task Reader(int rid) => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var list = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = sbucket }, cts.Token);
                    if (list.S3Objects is { Count: > 0 })
                    {
                        var pick = list.S3Objects[rid % list.S3Objects.Count];
                        using var got = await s3.GetObjectAsync(sbucket, pick.Key, cts.Token);
                        await got.ResponseStream.CopyToAsync(Stream.Null, cts.Token);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 404) { /* raced delete */ }
                catch (Exception ex) { failures.Add($"reader{rid}: {ex.GetType().Name}: {ex.Message}"); }
            }
        });

        Task GcLoop() => Task.Run(async () =>
        {
            for (var n = 0; !cts.IsCancellationRequested; n++)
            {
                try
                {
                    await RunGc(blobAgeSec: 0, uploadAgeSec: 86400);
                    await Task.Delay(gcInterval, cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { failures.Add($"gc#{n}: {ex.GetType().Name}: {ex.Message}"); }
            }
        });

        var writers = Enumerable.Range(0, writerTasks).Select(Writer).ToList();
        var background = Enumerable.Range(0, readerTasks).Select(Reader).Concat([GcLoop()]).ToList();
        await Task.WhenAll(writers);
        await cts.CancelAsync();
        await Task.WhenAll(background);

        if (!failures.IsEmpty)
            throw new InvalidOperationException($"{failures.Count} concurrent failures, first: {failures.First()}");

        var finalKeys = new HashSet<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = sbucket, ContinuationToken = token,
            });
            foreach (var o in page.S3Objects ?? []) finalKeys.Add(o.Key);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);

        var missing = writtenKeys.Keys.Where(k => !finalKeys.Contains(k)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{missing.Count} written keys missing from final list ({writtenKeys.Count} written, {finalKeys.Count} listed), e.g. '{missing[0]}'");
    }
    finally
    {
        try
        {
            string? token = null;
            do
            {
                var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = sbucket, ContinuationToken = token,
                });
                if (page.S3Objects is { Count: > 0 })
                {
                    await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = sbucket,
                        Objects = page.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    });
                }
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            } while (token is not null);
            await s3.DeleteBucketAsync(sbucket);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  cleanup warning: {ex.Message}"); }
    }
});

await Run("GcOrphanBlob", async () =>
{
    const string gbucket = "vessel3-realclient-gc";
    const string gkey = "orphan.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = gbucket });
    try
    {
        await RunGc(blobAgeSec: 0, uploadAgeSec: 86400);

        var payload = new byte[8192];
        new Random(7).NextBytes(payload);
        using (var ms = new MemoryStream(payload))
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = gbucket, Key = gkey, InputStream = ms, ContentType = "application/octet-stream",
            });
        }
        await s3.DeleteObjectAsync(gbucket, gkey);

        var after = await RunGc(blobAgeSec: 0, uploadAgeSec: 86400);
        if (after.blobsDeleted < 1)
            throw new InvalidOperationException($"GC reported {after.blobsDeleted} blobs deleted; expected at least one orphan from PUT+DELETE");

        var noop = await RunGc(blobAgeSec: 0, uploadAgeSec: 86400);
        if (noop.blobsDeleted != 0)
            throw new InvalidOperationException($"second GC should be no-op, got {noop.blobsDeleted}");
    }
    finally
    {
        try { await s3.DeleteBucketAsync(gbucket); } catch (Exception ex) { Console.Error.WriteLine($"  cleanup warning: {ex.Message}"); }
    }
});

await Run("GcKeepsVersionedBlobs", async () =>
{
    const string gbucket = "vessel3-realclient-gc-v";
    const string gkey = "vv.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = gbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = gbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        string v1Id;
        var v1Body = "first-version-payload";
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(v1Body)))
        {
            var p1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = gbucket, Key = gkey, InputStream = ms, ContentType = "text/plain" });
            v1Id = p1.VersionId;
        }
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("second-version-payload")))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = gbucket, Key = gkey, InputStream = ms, ContentType = "text/plain" });

        await RunGc(blobAgeSec: 0, uploadAgeSec: 86400);

        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = gbucket, Key = gkey, VersionId = v1Id });
        using var sr = new StreamReader(got.ResponseStream);
        if (await sr.ReadToEndAsync() != v1Body)
            throw new InvalidOperationException("v1 unreadable after GC; blob was wrongly reclaimed");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, gbucket);
    }
});

await Run("GcReapsAbandonedUploads", async () =>
{
    const string gbucket = "vessel3-realclient-gc-up";
    const string gkey = "abandoned.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = gbucket });
    try
    {
        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = gbucket, Key = gkey, ContentType = "application/octet-stream",
        });

        const int partSize = 5 * 1024 * 1024;
        var buf = new byte[partSize];
        new Random(42).NextBytes(buf);
        using (var ms = new MemoryStream(buf))
        {
            await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = gbucket, Key = gkey, UploadId = initiate.UploadId,
                PartNumber = 1, PartSize = partSize, InputStream = ms,
            });
        }

        var uploadsBefore = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = gbucket });
        if (uploadsBefore.MultipartUploads is null || uploadsBefore.MultipartUploads.All(u => u.UploadId != initiate.UploadId))
            throw new InvalidOperationException("upload missing before reap");

        var (_, reaped) = await RunGc(blobAgeSec: 0, uploadAgeSec: 0);
        if (reaped < 1)
            throw new InvalidOperationException($"expected >=1 upload reaped, got {reaped}");

        var uploadsAfter = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = gbucket });
        if (uploadsAfter.MultipartUploads?.Any(u => u.UploadId == initiate.UploadId) is true)
            throw new InvalidOperationException("upload still present after reap");
    }
    finally
    {
        try { await s3.DeleteBucketAsync(gbucket); } catch (Exception ex) { Console.Error.WriteLine($"  cleanup warning: {ex.Message}"); }
    }
});

await Run("BucketVersioning", async () =>
{
    const string vbucket = "vessel3-realclient-versioning";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        var initial = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = vbucket });
        var initialStatus = initial.VersioningConfig?.Status;
        if (initialStatus is not null && initialStatus != Amazon.S3.VersionStatus.Off)
            throw new InvalidOperationException($"new bucket should be unversioned; got '{initialStatus}'");

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        var after = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = vbucket });
        if (after.VersioningConfig?.Status != Amazon.S3.VersionStatus.Enabled)
            throw new InvalidOperationException($"after enabling, status was '{after.VersioningConfig?.Status}'");
    }
    finally
    {
        await s3.DeleteBucketAsync(vbucket);
    }
});

await Run("PresignedGet", async () =>
{
    var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = bucket,
        Key = key,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = Protocol.HTTP,
    });

    using var http = new HttpClient();
    var resp = await http.GetAsync(url);
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"presigned GET failed: {(int)resp.StatusCode} {resp.ReasonPhrase} url={url}");
    var got = await resp.Content.ReadAsStringAsync();
    if (got != body)
        throw new InvalidOperationException($"presigned GET body mismatch: got '{got}', expected '{body}'");
});

await Run("PresignedPut", async () =>
{
    const string presignedKey = "presigned-put.txt";
    const string presignedBody = "presigned-put-body";
    var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = bucket,
        Key = presignedKey,
        Verb = HttpVerb.PUT,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = Protocol.HTTP,
    });

    using var http = new HttpClient();
    using var content = new StringContent(presignedBody);
    var resp = await http.PutAsync(url, content);
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"presigned PUT failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

    using var getResp = await s3.GetObjectAsync(bucket, presignedKey);
    using var sr = new StreamReader(getResp.ResponseStream);
    var stored = await sr.ReadToEndAsync();
    if (stored != presignedBody)
        throw new InvalidOperationException($"presigned PUT body mismatch: got '{stored}', expected '{presignedBody}'");

    await s3.DeleteObjectAsync(bucket, presignedKey);
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
    int[] uploadSequence = [1, 0, 2];
    foreach (var i in uploadSequence)
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
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpBadEtagKey,
        UploadId = initiate.UploadId,
    });
});

const string mpUploadPartCopyKey = "multipart-uploadpartcopy.bin";

await Run("UploadPartCopy", async () =>
{
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpUploadPartCopyKey,
        ContentType = "application/octet-stream",
    });

    var part1 = await s3.CopyPartAsync(new CopyPartRequest
    {
        SourceBucket = bucket, SourceKey = mpManualKey,
        DestinationBucket = bucket, DestinationKey = mpUploadPartCopyKey,
        UploadId = initiate.UploadId,
        PartNumber = 1,
        FirstByte = 0, LastByte = 5 * 1024 * 1024 - 1,
    });

    var part2 = await s3.CopyPartAsync(new CopyPartRequest
    {
        SourceBucket = bucket, SourceKey = mpManualKey,
        DestinationBucket = bucket, DestinationKey = mpUploadPartCopyKey,
        UploadId = initiate.UploadId,
        PartNumber = 2,
        FirstByte = 5 * 1024 * 1024, LastByte = 10 * 1024 * 1024 - 1,
    });

    await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpUploadPartCopyKey,
        UploadId = initiate.UploadId,
        PartETags = [
            new PartETag(1, part1.ETag),
            new PartETag(2, part2.ETag),
        ],
    });

    using var src = await s3.GetObjectAsync(new GetObjectRequest
    {
        BucketName = bucket, Key = mpManualKey,
        ByteRange = new ByteRange(0, 10 * 1024 * 1024 - 1),
    });
    using var srcSink = new MemoryStream();
    await src.ResponseStream.CopyToAsync(srcSink);

    using var dst = await s3.GetObjectAsync(bucket, mpUploadPartCopyKey);
    using var dstSink = new MemoryStream();
    await dst.ResponseStream.CopyToAsync(dstSink);

    if (!srcSink.ToArray().AsSpan().SequenceEqual(dstSink.ToArray()))
        throw new InvalidOperationException("UploadPartCopy bytes mismatch");
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
        }
    }

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket,
        Key = mpDupKey,
        UploadId = initiate.UploadId,
    });
});

await Run("ListMultipartUploads", async () =>
{
    const string listKey = "multipart-list-test.bin";
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = bucket, Key = listKey, ContentType = "application/octet-stream",
    });

    const int partSize = 5 * 1024 * 1024;
    var buf = new byte[partSize];
    new Random(55).NextBytes(buf);
    using (var ms = new MemoryStream(buf))
    {
        await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = listKey, UploadId = initiate.UploadId,
            PartNumber = 1, PartSize = partSize, InputStream = ms,
        });
    }

    var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
    var found = uploads.MultipartUploads?.FirstOrDefault(u => u.UploadId == initiate.UploadId);
    if (found is null) throw new InvalidOperationException($"upload {initiate.UploadId} missing from ListMultipartUploads");
    if (found.Key != listKey) throw new InvalidOperationException($"listed key '{found.Key}' != '{listKey}'");

    var parts = await s3.ListPartsAsync(new ListPartsRequest
    {
        BucketName = bucket, Key = listKey, UploadId = initiate.UploadId,
    });
    if (parts.Parts is null || parts.Parts.Count is 0)
        throw new InvalidOperationException("ListParts returned no parts");
    if (parts.Parts[0].PartNumber != 1)
        throw new InvalidOperationException($"part number was {parts.Parts[0].PartNumber}, expected 1");
    if (parts.Parts[0].Size != partSize)
        throw new InvalidOperationException($"part size was {parts.Parts[0].Size}, expected {partSize}");

    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
    {
        BucketName = bucket, Key = listKey, UploadId = initiate.UploadId,
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
            new KeyVersion { Key = mpUploadPartCopyKey },
            new KeyVersion { Key = mpTransferKey },
        ],
    });
    if (r.DeletedObjects is null || r.DeletedObjects.Count != 7)
        throw new InvalidOperationException($"expected 7 deleted, got {r.DeletedObjects?.Count ?? 0}");
});

await Run("DeleteBucket",   () => s3.DeleteBucketAsync(bucket));

await Run("ConcurrentPut", async () =>
{
    const string cbucket = "vessel3-realclient-concurrent";
    const string ckey = "race.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = cbucket });
    try
    {
        var tasks = Enumerable.Range(0, 16).Select(i =>
        {
            var payload = Encoding.UTF8.GetBytes($"writer-{i}");
            return s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = cbucket, Key = ckey,
                InputStream = new MemoryStream(payload),
                ContentType = "application/octet-stream",
            });
        }).ToArray();
        await Task.WhenAll(tasks);

        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = cbucket });
        var matches = listed.S3Objects?.Count(o => o.Key == ckey) ?? 0;
        if (matches != 1)
            throw new InvalidOperationException($"unversioned bucket has {matches} rows for '{ckey}' after concurrent puts; expected 1");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, cbucket);
    }
});

await Run("MultipartCopyEtag", async () =>
{
    const string mbucket = "vessel3-realclient-mpcopy";
    const string srcKey = "mp-src.bin";
    const string dstKey = "mp-dst.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = mbucket });
    try
    {
        const int partSize = 5 * 1024 * 1024;
        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = mbucket, Key = srcKey, ContentType = "application/octet-stream",
        });
        var etags = new List<PartETag>();
        var rng = new Random(7);
        for (var i = 0; i < 2; i++)
        {
            var buf = new byte[partSize];
            rng.NextBytes(buf);
            using var ms = new MemoryStream(buf);
            var up = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = mbucket, Key = srcKey, UploadId = initiate.UploadId,
                PartNumber = i + 1, PartSize = partSize, InputStream = ms,
            });
            etags.Add(new PartETag(i + 1, up.ETag));
        }
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = mbucket, Key = srcKey, UploadId = initiate.UploadId, PartETags = etags,
        });

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = mbucket, SourceKey = srcKey,
            DestinationBucket = mbucket, DestinationKey = dstKey,
        });

        var head = await s3.GetObjectMetadataAsync(mbucket, dstKey);
        var etag = head.ETag.Trim('"');
        if (!etag.EndsWith("-2", StringComparison.Ordinal))
            throw new InvalidOperationException($"multipart copy dest ETag '{etag}' lacks '-N' suffix");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, mbucket);
    }
});

await Run("IfMatchList", async () =>
{
    const string ibucket = "vessel3-realclient-ifmatch";
    const string ikey = "im.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = ibucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("v1"));
        var first = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = ibucket, Key = ikey, InputStream = ms, ContentType = "text/plain",
        });
        var realEtag = first.ETag.Trim('"');

        var listMatchesOne = $"\"bogus0000000000000000000000000000\", \"{realEtag}\"";
        var listMatchesNone = "\"bogus0000000000000000000000000000\", \"bogus1111111111111111111111111111\"";

        using (var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("v2")))
        {
            var req = new PutObjectRequest
            {
                BucketName = ibucket, Key = ikey, InputStream = ms2, ContentType = "text/plain",
            };
            req.Headers["If-Match"] = listMatchesOne;
            await s3.PutObjectAsync(req);
        }

        try
        {
            using var ms3 = new MemoryStream(Encoding.UTF8.GetBytes("v3"));
            var req = new PutObjectRequest
            {
                BucketName = ibucket, Key = ikey, InputStream = ms3, ContentType = "text/plain",
            };
            req.Headers["If-Match"] = listMatchesNone;
            await s3.PutObjectAsync(req);
            throw new InvalidOperationException("PUT with If-Match list of non-matching tags should have failed");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 412)
        {
        }
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, ibucket);
    }
});

await Run("ListVersionsPaging", async () =>
{
    const string vbucket = "vessel3-realclient-vpag";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = vbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = vbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        for (var i = 0; i < 5; i++)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes($"v{i}"));
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = vbucket, Key = $"k{i:D2}.bin",
                InputStream = ms, ContentType = "application/octet-stream",
            });
        }

        var first = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = vbucket, MaxKeys = 2 });
        if (first.IsTruncated != true)
            throw new InvalidOperationException("ListVersions with MaxKeys=2 should be truncated for 5 versions");
        if (string.IsNullOrEmpty(first.NextKeyMarker))
            throw new InvalidOperationException("truncated ListVersions response missing NextKeyMarker");
        var firstCount = first.Versions?.Count ?? 0;
        if (firstCount != 2)
            throw new InvalidOperationException($"first page returned {firstCount} entries, expected 2");

        var next = await s3.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = vbucket, MaxKeys = 2, KeyMarker = first.NextKeyMarker,
        });
        if (next.Versions is null || next.Versions.Count == 0)
            throw new InvalidOperationException("follow-up ListVersions page empty");
        if (next.Versions[0].Key == first.Versions![0].Key)
            throw new InvalidOperationException("follow-up page returned a key from the first page");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, vbucket);
    }
});

await Run("GetObjectAttributesSinglePart", async () =>
{
    const string abucket = "vessel3-realclient-attrs-sp";
    const string akey = "sp.txt";
    const string payload = "hello-attrs";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = abucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = abucket, Key = akey, InputStream = ms, ContentType = "text/plain",
        });
        var rawEtag = put.ETag.Trim('"');

        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = abucket,
            Key = akey,
            ObjectAttributes = ["ETag", "ObjectSize", "StorageClass", "Checksum"],
        });
        if (attrs.ETag is null || attrs.ETag.Trim('"') != rawEtag)
            throw new InvalidOperationException($"attrs ETag '{attrs.ETag}' != '{rawEtag}'");
        if (attrs.ObjectSize != payload.Length)
            throw new InvalidOperationException($"attrs ObjectSize {attrs.ObjectSize} != {payload.Length}");
        if (attrs.ObjectParts is not null && attrs.ObjectParts.TotalPartsCount > 0)
            throw new InvalidOperationException("single-part object should not report ObjectParts");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, abucket);
    }
});

await Run("GetObjectAttributesMultipart", async () =>
{
    const string abucket = "vessel3-realclient-attrs-mp";
    const string akey = "mp.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = abucket });
    try
    {
        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = abucket, Key = akey, ContentType = "application/octet-stream",
        });
        const int partSize = 5 * 1024 * 1024;
        var etags = new List<PartETag>();
        var rng = new Random(101);
        for (var i = 0; i < 2; i++)
        {
            var buf = new byte[partSize];
            rng.NextBytes(buf);
            using var ms = new MemoryStream(buf);
            var up = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = abucket, Key = akey, UploadId = initiate.UploadId,
                PartNumber = i + 1, PartSize = partSize, InputStream = ms,
            });
            etags.Add(new PartETag(i + 1, up.ETag));
        }
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = abucket, Key = akey, UploadId = initiate.UploadId, PartETags = etags,
        });

        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = abucket,
            Key = akey,
            ObjectAttributes = ["ObjectParts", "ObjectSize"],
        });
        if (attrs.ObjectParts is null || attrs.ObjectParts.TotalPartsCount != 2)
            throw new InvalidOperationException($"multipart attrs missing ObjectParts (got count {attrs.ObjectParts?.TotalPartsCount})");
        if (attrs.ObjectSize != partSize * 2L)
            throw new InvalidOperationException($"multipart attrs ObjectSize {attrs.ObjectSize} != {partSize * 2L}");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, abucket);
    }
});

await Run("RangeSuffix", async () =>
{
    const string rbucket = "vessel3-realclient-rsuffix";
    const string rkey = "r.txt";
    var body = "0123456789ABCDEF";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = rbucket });
    try
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = rbucket, Key = rkey, InputStream = ms, ContentType = "text/plain" });

        using var http = new HttpClient();
        var presign = new GetPreSignedUrlRequest
        {
            BucketName = rbucket, Key = rkey, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Protocol.HTTP,
        };
        var url = await s3.GetPreSignedURLAsync(presign);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Range", "bytes=-4");
        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode != 206)
            throw new InvalidOperationException($"suffix range expected 206, got {(int)resp.StatusCode}");
        var got = await resp.Content.ReadAsStringAsync();
        if (got != "CDEF") throw new InvalidOperationException($"suffix range body '{got}' != 'CDEF'");
    }
    finally { await DurabilityTester.CleanupBucket(s3, rbucket); }
});

await Run("RangeOpenEnd", async () =>
{
    const string rbucket = "vessel3-realclient-ropen";
    const string rkey = "r.txt";
    var body = "0123456789ABCDEF";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = rbucket });
    try
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = rbucket, Key = rkey, InputStream = ms, ContentType = "text/plain" });

        using var http = new HttpClient();
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = rbucket, Key = rkey, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Protocol.HTTP,
        });
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Range", "bytes=10-");
        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode != 206)
            throw new InvalidOperationException($"open-end range expected 206, got {(int)resp.StatusCode}");
        var got = await resp.Content.ReadAsStringAsync();
        if (got != "ABCDEF") throw new InvalidOperationException($"open-end body '{got}' != 'ABCDEF'");
    }
    finally { await DurabilityTester.CleanupBucket(s3, rbucket); }
});

await Run("RangeUnsatisfiable", async () =>
{
    const string rbucket = "vessel3-realclient-runsat";
    const string rkey = "r.txt";
    var body = "0123456789";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = rbucket });
    try
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = rbucket, Key = rkey, InputStream = ms, ContentType = "text/plain" });

        using var http = new HttpClient();
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = rbucket, Key = rkey, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Protocol.HTTP,
        });
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Range", "bytes=100-200");
        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode != 416)
            throw new InvalidOperationException($"unsatisfiable range expected 416, got {(int)resp.StatusCode}");
        string contentRange = resp.Content.Headers.ContentRange?.ToString() ?? "";
        if (string.IsNullOrEmpty(contentRange) && resp.Headers.TryGetValues("Content-Range", out var crv))
            contentRange = string.Join(",", crv);
        if (string.IsNullOrEmpty(contentRange) || !contentRange.Contains("*/", StringComparison.Ordinal))
            throw new InvalidOperationException($"416 must include Content-Range bytes */size; got '{contentRange}'");
    }
    finally { await DurabilityTester.CleanupBucket(s3, rbucket); }
});

await Run("RangeMultiFullObject", async () =>
{
    const string rbucket = "vessel3-realclient-rmulti";
    const string rkey = "r.txt";
    var body = "0123456789ABCDEF";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = rbucket });
    try
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = rbucket, Key = rkey, InputStream = ms, ContentType = "text/plain" });

        using var http = new HttpClient();
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = rbucket, Key = rkey, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Protocol.HTTP,
        });
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Range", "bytes=0-3,10-13");
        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode != 200)
            throw new InvalidOperationException($"multi-range expected full 200, got {(int)resp.StatusCode}");
        var got = await resp.Content.ReadAsStringAsync();
        if (got != body) throw new InvalidOperationException($"multi-range full body '{got}' != '{body}'");
    }
    finally { await DurabilityTester.CleanupBucket(s3, rbucket); }
});

await Run("SuspendedVersioning", async () =>
{
    const string sbucket = "vessel3-realclient-susp";
    const string skey = "s.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = sbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = sbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        string v1Id, v2Id;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v1")))
            v1Id = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = sbucket, Key = skey, InputStream = ms, ContentType = "text/plain" })).VersionId;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v2")))
            v2Id = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = sbucket, Key = skey, InputStream = ms, ContentType = "text/plain" })).VersionId;

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = sbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Suspended },
        });
        string suspId;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("suspended-1")))
            suspId = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = sbucket, Key = skey, InputStream = ms, ContentType = "text/plain" })).VersionId;
        if (suspId != "null")
            throw new InvalidOperationException($"Suspended PUT versionId '{suspId}' != 'null'");

        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("suspended-2")))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = sbucket, Key = skey, InputStream = ms, ContentType = "text/plain" });

        var lv = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = sbucket });
        var versions = lv.Versions ?? [];
        var ids = versions.Select(v => v.VersionId).ToList();
        var nullCount = ids.Count(id => id == "null");
        if (nullCount != 1)
            throw new InvalidOperationException($"expected exactly one 'null' version, got {nullCount} (ids: {string.Join(",", ids)})");
        if (!ids.Contains(v1Id) || !ids.Contains(v2Id))
            throw new InvalidOperationException($"Enabled-era versions lost; ids: {string.Join(",", ids)}");

        using var got = await s3.GetObjectAsync(sbucket, skey);
        using var sr = new StreamReader(got.ResponseStream);
        var latestBody = await sr.ReadToEndAsync();
        if (latestBody != "suspended-2")
            throw new InvalidOperationException($"latest GET '{latestBody}' != 'suspended-2'");

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = sbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v3")))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = sbucket, Key = skey, InputStream = ms, ContentType = "text/plain" });

        var lv2 = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = sbucket });
        var ids2 = (lv2.Versions ?? []).Select(v => v.VersionId).ToList();
        if (!ids2.Contains(v1Id) || !ids2.Contains(v2Id) || !ids2.Contains("null"))
            throw new InvalidOperationException($"Re-enable lost prior versions; ids: {string.Join(",", ids2)}");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, sbucket);
    }
});

await Run("PutTagging", async () =>
{
    const string tbucket = "vessel3-realclient-tag1";
    const string tkey = "obj.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
        });

        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = tbucket, Key = tkey,
            Tagging = new Tagging
            {
                TagSet =
                [
                    new Tag { Key = "env", Value = "prod" },
                    new Tag { Key = "team", Value = "platform" },
                ],
            },
        });

        var got = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = tkey });
        var dict = got.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (dict["env"] != "prod" || dict["team"] != "platform")
            throw new InvalidOperationException("tag round-trip mismatch");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

await Run("GetTagging", async () =>
{
    const string tbucket = "vessel3-realclient-tag2";
    const string tkey = "obj.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
        });

        var got = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = tkey });
        if ((got.Tagging?.Count ?? 0) != 0)
            throw new InvalidOperationException($"expected 0 tags initially, got {got.Tagging?.Count ?? 0}");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

await Run("DeleteTagging", async () =>
{
    const string tbucket = "vessel3-realclient-tag3";
    const string tkey = "obj.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
        });
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = tbucket, Key = tkey,
            Tagging = new Tagging { TagSet = [new Tag { Key = "a", Value = "1" }] },
        });

        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = tbucket, Key = tkey });
        var got = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = tkey });
        if ((got.Tagging?.Count ?? 0) != 0)
            throw new InvalidOperationException($"expected 0 tags after delete, got {got.Tagging?.Count ?? 0}");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

await Run("PutWithTaggingHeader", async () =>
{
    const string tbucket = "vessel3-realclient-tag4";
    const string tkey = "obj.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
            TagSet = [new Tag { Key = "env", Value = "stg" }, new Tag { Key = "k 2", Value = "v 2" }],
        });

        var got = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = tkey });
        var dict = got.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (dict["env"] != "stg" || dict["k 2"] != "v 2")
            throw new InvalidOperationException("x-amz-tagging header did not seed tags");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

await Run("CopyTaggingDirective", async () =>
{
    const string tbucket = "vessel3-realclient-tag5";
    const string srcKey = "src.txt";
    const string dstCopy = "dst-copy.txt";
    const string dstReplace = "dst-replace.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("hi")))
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = tbucket, Key = srcKey, InputStream = ms, ContentType = "text/plain",
                TagSet = [new Tag { Key = "origin", Value = "src" }],
            });
        }

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = tbucket, SourceKey = srcKey,
            DestinationBucket = tbucket, DestinationKey = dstCopy,
        });
        var inherited = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = dstCopy });
        var inhMap = inherited.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (inhMap.GetValueOrDefault("origin") != "src")
            throw new InvalidOperationException($"default COPY did not inherit source tags (got {inherited.Tagging?.Count ?? 0})");

        var copyReplaceReq = new CopyObjectRequest
        {
            SourceBucket = tbucket, SourceKey = srcKey,
            DestinationBucket = tbucket, DestinationKey = dstReplace,
#if !AWSSDK_V3
            TaggingDirective = TaggingDirective.REPLACE,
#endif
            TagSet = [new Tag { Key = "origin", Value = "copy" }],
        };
        await s3.CopyObjectAsync(copyReplaceReq);
        var replaced = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = tbucket, Key = dstReplace });
        var repMap = replaced.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (repMap.GetValueOrDefault("origin") != "copy")
            throw new InvalidOperationException($"REPLACE directive did not apply (got origin={repMap.GetValueOrDefault("origin")})");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

await Run("VersionedTagging", async () =>
{
    const string tbucket = "vessel3-realclient-tag6";
    const string tkey = "obj.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = tbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = tbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });

        string v1, v2;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("a")))
        {
            var p = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
                TagSet = [new Tag { Key = "ver", Value = "1" }],
            });
            v1 = p.VersionId;
        }
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("b")))
        {
            var p = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = tbucket, Key = tkey, InputStream = ms, ContentType = "text/plain",
                TagSet = [new Tag { Key = "ver", Value = "2" }],
            });
            v2 = p.VersionId;
        }

        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = tbucket, Key = tkey, VersionId = v1,
            Tagging = new Tagging { TagSet = [new Tag { Key = "ver", Value = "1-edited" }] },
        });

        var gotV1 = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = tbucket, Key = tkey, VersionId = v1,
        });
        var v1Map = gotV1.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (v1Map["ver"] != "1-edited")
            throw new InvalidOperationException($"v1 tags: got '{v1Map["ver"]}', expected '1-edited'");

        var gotV2 = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = tbucket, Key = tkey, VersionId = v2,
        });
        var v2Map = gotV2.Tagging?.ToDictionary(t => t.Key, t => t.Value) ?? new();
        if (v2Map["ver"] != "2")
            throw new InvalidOperationException($"v2 tags should be unchanged; got '{v2Map["ver"]}'");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, tbucket);
    }
});

const string ckBucket = "vessel3-realclient-checksum";
await s3.PutBucketAsync(new PutBucketRequest { BucketName = ckBucket });

static string Crc32B64(byte[] data) =>
    Convert.ToBase64String(BitConverter.GetBytes(System.IO.Hashing.Crc32.HashToUInt32(data)).Reverse().ToArray());
static string Crc32CB64(byte[] data)
{
    uint c = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        c ^= b;
        for (var i = 0; i < 8; i++)
            c = (c & 1) != 0 ? (0x82F63B78u ^ (c >> 1)) : (c >> 1);
    }
    var v = c ^ 0xFFFFFFFFu;
    var bytes = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    return Convert.ToBase64String(bytes);
}
static string Sha1B64(byte[] data) => Convert.ToBase64String(SHA1.HashData(data));
static string Sha256B64(byte[] data) => Convert.ToBase64String(SHA256.HashData(data));

await Run("ChecksumCrc32", async () =>
{
    const string ckey = "checksum-crc32.txt";
    var body = Encoding.UTF8.GetBytes("crc32 payload");
    var put = new PutObjectRequest
    {
        BucketName = ckBucket, Key = ckey,
        InputStream = new MemoryStream(body), ContentType = "text/plain",
        DisableDefaultChecksumValidation = true,
    };
    put.Headers["x-amz-checksum-crc32"] = Crc32B64(body);
    await s3.PutObjectAsync(put);

    var head = await s3.GetObjectMetadataAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(head.ChecksumCRC32))
        throw new InvalidOperationException("HEAD missing x-amz-checksum-crc32");

    using var got = await s3.GetObjectAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(got.ChecksumCRC32))
        throw new InvalidOperationException("GET missing x-amz-checksum-crc32");

    await s3.DeleteObjectAsync(ckBucket, ckey);
});

await Run("ChecksumCrc32C", async () =>
{
    const string ckey = "checksum-crc32c.txt";
    var body = Encoding.UTF8.GetBytes("crc32c payload");
    var put = new PutObjectRequest
    {
        BucketName = ckBucket, Key = ckey,
        InputStream = new MemoryStream(body), ContentType = "text/plain",
        DisableDefaultChecksumValidation = true,
    };
    put.Headers["x-amz-checksum-crc32c"] = Crc32CB64(body);
    await s3.PutObjectAsync(put);

    var head = await s3.GetObjectMetadataAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(head.ChecksumCRC32C))
        throw new InvalidOperationException("HEAD missing x-amz-checksum-crc32c");

    using var got = await s3.GetObjectAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(got.ChecksumCRC32C))
        throw new InvalidOperationException("GET missing x-amz-checksum-crc32c");

    await s3.DeleteObjectAsync(ckBucket, ckey);
});

await Run("ChecksumSha1", async () =>
{
    const string ckey = "checksum-sha1.txt";
    var body = Encoding.UTF8.GetBytes("sha1 payload");
    var put = new PutObjectRequest
    {
        BucketName = ckBucket, Key = ckey,
        InputStream = new MemoryStream(body), ContentType = "text/plain",
        DisableDefaultChecksumValidation = true,
    };
    put.Headers["x-amz-checksum-sha1"] = Sha1B64(body);
    await s3.PutObjectAsync(put);

    var head = await s3.GetObjectMetadataAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(head.ChecksumSHA1))
        throw new InvalidOperationException("HEAD missing x-amz-checksum-sha1");

    using var got = await s3.GetObjectAsync(ckBucket, ckey);
    if (string.IsNullOrEmpty(got.ChecksumSHA1))
        throw new InvalidOperationException("GET missing x-amz-checksum-sha1");

    await s3.DeleteObjectAsync(ckBucket, ckey);
});

await Run("ChecksumSha256Roundtrip", async () =>
{
    const string ckey = "checksum-sha256.txt";
    var body = Encoding.UTF8.GetBytes("sha256 payload");
    var expected = Sha256B64(body);
    var put = new PutObjectRequest
    {
        BucketName = ckBucket, Key = ckey,
        InputStream = new MemoryStream(body), ContentType = "text/plain",
        DisableDefaultChecksumValidation = true,
    };
    put.Headers["x-amz-checksum-sha256"] = expected;
    await s3.PutObjectAsync(put);

    var head = await s3.GetObjectMetadataAsync(ckBucket, ckey);
    if (head.ChecksumSHA256 != expected)
        throw new InvalidOperationException($"HEAD sha256 '{head.ChecksumSHA256}' != expected '{expected}'");

    using var got = await s3.GetObjectAsync(ckBucket, ckey);
    if (got.ChecksumSHA256 != expected)
        throw new InvalidOperationException($"GET sha256 '{got.ChecksumSHA256}' != expected '{expected}'");

    await s3.DeleteObjectAsync(ckBucket, ckey);
});

await Run("MultipartCompositeChecksum", async () =>
{
    const string ckey = "checksum-multipart.bin";
    var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
    {
        BucketName = ckBucket, Key = ckey, ContentType = "application/octet-stream",
        ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
    });

    const int partSize = 5 * 1024 * 1024;
    var rng = new Random(2024);
    var etags = new List<PartETag>();
    for (var i = 0; i < 2; i++)
    {
        var buf = new byte[partSize];
        rng.NextBytes(buf);
        using var ms = new MemoryStream(buf);
        var partRes = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = ckBucket, Key = ckey, UploadId = initiate.UploadId,
            PartNumber = i + 1, PartSize = partSize, InputStream = ms,
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        });
        etags.Add(new PartETag(i + 1, partRes.ETag) { ChecksumCRC32 = partRes.ChecksumCRC32 });
    }

    var complete = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
    {
        BucketName = ckBucket, Key = ckey, UploadId = initiate.UploadId, PartETags = etags,
        ChecksumType = ChecksumType.COMPOSITE,
    });
    if (string.IsNullOrEmpty(complete.ChecksumCRC32))
        throw new InvalidOperationException("CompleteMultipartUpload missing x-amz-checksum-crc32");
    if (!complete.ChecksumCRC32.EndsWith("-2", StringComparison.Ordinal))
        throw new InvalidOperationException($"composite checksum '{complete.ChecksumCRC32}' lacks '-2' suffix");

    await s3.DeleteObjectAsync(ckBucket, ckey);
});

await s3.DeleteBucketAsync(ckBucket);

await Run("ListEncodingTypeUrl", async () =>
{
    const string ebucket = "vessel3-realclient-encoding";
    const string spaceKey = "dir one/file two.txt";
    const string unicodeKey = "café/résumé.txt";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = ebucket });
    try
    {
        foreach (var k in new[] { spaceKey, unicodeKey })
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = ebucket, Key = k, InputStream = ms, ContentType = "text/plain",
            });
        }

        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = ebucket,
            Encoding = EncodingType.Url,
        });
        if (listed.Encoding != EncodingType.Url)
            throw new InvalidOperationException("response did not echo EncodingType=url");

        var keys = listed.S3Objects?.Select(o => Uri.UnescapeDataString(o.Key)).ToHashSet() ?? new HashSet<string>();
        if (!keys.Contains(spaceKey))
            throw new InvalidOperationException($"space key '{spaceKey}' did not round-trip; got [{string.Join(", ", keys)}]");
        if (!keys.Contains(unicodeKey))
            throw new InvalidOperationException($"unicode key '{unicodeKey}' did not round-trip; got [{string.Join(", ", keys)}]");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, ebucket);
    }
});

await Run("ObjectLockRequiresVersioning", async () =>
{
    const string lbucket = "vessel3-realclient-lock-req";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        try
        {
            await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
            {
                BucketName = lbucket,
                ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration
                {
                    ObjectLockEnabled = ObjectLockEnabled.Enabled,
                },
            });
            throw new InvalidOperationException("expected 409 InvalidBucketState when enabling Object Lock without versioning");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 409)
        {
        }
    }
    finally
    {
        try { await s3.DeleteBucketAsync(lbucket); } catch { /* ignore */ }
    }
});

await Run("ObjectLockConfig", async () =>
{
    const string lbucket = "vessel3-realclient-lock-cfg";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule
                {
                    DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Governance, Days = 5 },
                },
            },
        });
        var got = await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = lbucket });
        if (got.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled)
            throw new InvalidOperationException("GetObjectLockConfiguration did not echo Enabled");
        if (got.ObjectLockConfiguration.Rule?.DefaultRetention?.Days != 5)
            throw new InvalidOperationException($"Default Days mismatch: got {got.ObjectLockConfiguration.Rule?.DefaultRetention?.Days}");
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

await Run("RetentionGovernance", async () =>
{
    const string lbucket = "vessel3-realclient-ret-gov";
    const string lkey = "g.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration { ObjectLockEnabled = ObjectLockEnabled.Enabled },
        });

        string vid;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("gov")))
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = lbucket, Key = lkey, InputStream = ms, ContentType = "application/octet-stream",
            });
            vid = put.VersionId;
        }

        var until = DateTime.UtcNow.AddDays(10);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = until },
        });

        var got = await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
        });
        if (got.Retention?.Mode != ObjectLockRetentionMode.Governance)
            throw new InvalidOperationException($"retention mode mismatch: {got.Retention?.Mode}");

        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
            throw new InvalidOperationException("expected 403 AccessDenied for unretired GOVERNANCE delete");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 403)
        {
        }
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

await Run("RetentionCompliance", async () =>
{
    const string lbucket = "vessel3-realclient-ret-comp";
    const string lkey = "c.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration { ObjectLockEnabled = ObjectLockEnabled.Enabled },
        });

        string vid;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("comp")))
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = lbucket, Key = lkey, InputStream = ms, ContentType = "application/octet-stream",
            });
            vid = put.VersionId;
        }

        var until = DateTime.UtcNow.AddDays(10);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = until },
        });

        try
        {
            var lowerReq = new PutObjectRetentionRequest
            {
                BucketName = lbucket, Key = lkey, VersionId = vid, BypassGovernanceRetention = true,
                Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = until.AddDays(-1) },
            };
            await s3.PutObjectRetentionAsync(lowerReq);
            throw new InvalidOperationException("expected 403 lowering COMPLIANCE retention");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 403)
        {
        }

        try
        {
            var del = new DeleteObjectRequest { BucketName = lbucket, Key = lkey, VersionId = vid, BypassGovernanceRetention = true };
            await s3.DeleteObjectAsync(del);
            throw new InvalidOperationException("expected 403 deleting COMPLIANCE-locked version");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 403)
        {
        }
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

await Run("BypassGovernanceDelete", async () =>
{
    const string lbucket = "vessel3-realclient-bypass";
    const string lkey = "by.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration { ObjectLockEnabled = ObjectLockEnabled.Enabled },
        });

        string vid;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("bypass")))
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = lbucket, Key = lkey, InputStream = ms, ContentType = "application/octet-stream",
            });
            vid = put.VersionId;
        }

        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = DateTime.UtcNow.AddDays(10) },
        });

        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid, BypassGovernanceRetention = true,
        });
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

await Run("LegalHold", async () =>
{
    const string lbucket = "vessel3-realclient-hold";
    const string lkey = "h.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration { ObjectLockEnabled = ObjectLockEnabled.Enabled },
        });

        string vid;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("hold")))
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = lbucket, Key = lkey, InputStream = ms, ContentType = "application/octet-stream",
            });
            vid = put.VersionId;
        }

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        });

        var got = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
        if (got.LegalHold?.Status != ObjectLockLegalHoldStatus.On)
            throw new InvalidOperationException($"expected ON, got '{got.LegalHold?.Status}'");

        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
            throw new InvalidOperationException("expected 403 deleting legal-held version");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 403)
        {
        }

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
        });
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

await Run("DefaultRetentionAppliedOnPut", async () =>
{
    const string lbucket = "vessel3-realclient-def-ret";
    const string lkey = "d.bin";
    await s3.PutBucketAsync(new PutBucketRequest { BucketName = lbucket });
    try
    {
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = lbucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = Amazon.S3.VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = lbucket,
            ObjectLockConfiguration = new Amazon.S3.Model.ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule
                {
                    DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Governance, Days = 3 },
                },
            },
        });

        string vid;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("default")))
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = lbucket, Key = lkey, InputStream = ms, ContentType = "application/octet-stream",
            });
            vid = put.VersionId;
        }

        var got = await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
        if (got.Retention?.Mode != ObjectLockRetentionMode.Governance)
            throw new InvalidOperationException($"default retention not applied: mode {got.Retention?.Mode}");

        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = lbucket, Key = lkey, VersionId = vid });
            throw new InvalidOperationException("expected 403 deleting default-retention-locked version");
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 403)
        {
        }

        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = lbucket, Key = lkey, VersionId = vid, BypassGovernanceRetention = true,
        });
    }
    finally
    {
        await DurabilityTester.CleanupBucket(s3, lbucket);
    }
});

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
