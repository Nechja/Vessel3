using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

internal static class CorsAndAccessScenarios
{
    private const string Bucket = "vessel3-cors-access-probe";
    private const string Key = "cors-test.txt";
    private const string Body = "Testing CORS and Access Flags in Vessel3!";

    public static async Task<int> Run(AmazonS3Client s3, string endpoint)
    {
        Console.WriteLine("--- CORS and Bucket Access Scenarios ---");

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };

        await DurabilityTester.CleanupBucket(s3, Bucket);

        try
        {
            await SetupBucketAndObject(s3);
            await TestCorsSdkLifecycle(s3);
            await TestHttpCorsPreflight(http);
            await TestHttpCorsActualRequest(http);
            await TestPublicReadAndPrivateAccess(s3, http);
            await TestDeleteCors(s3);

            Console.WriteLine("--- CORS and Access Scenarios OK ---");
            return 0;
        }
        finally
        {
            await DurabilityTester.CleanupBucket(s3, Bucket);
        }
    }

    private static async Task SetupBucketAndObject(AmazonS3Client s3)
    {
        await Step("CreateBucket", async () =>
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        });

        await Step("PutObject", async () =>
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Body));
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = Key,
                InputStream = ms,
                ContentType = "text/plain",
            });
        });
    }

    private static async Task TestCorsSdkLifecycle(AmazonS3Client s3)
    {
        await Step("PutBucketCors", async () =>
        {
            await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = Bucket,
                Configuration = new CORSConfiguration
                {
                    Rules =
                    [
                        new CORSRule
                        {
                            Id = "Rule1",
                            AllowedOrigins = ["http://localhost:3000", "https://*.example.com"],
                            AllowedMethods = ["GET", "PUT", "DELETE"],
                            AllowedHeaders = ["*"],
                            ExposeHeaders = ["ETag", "x-amz-request-id"],
                            MaxAgeSeconds = 3600,
                        }
                    ]
                }
            });
        });

        await Step("GetBucketCors", async () =>
        {
            var res = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = Bucket });
            if (res.Configuration.Rules.Count != 1)
                throw new InvalidOperationException($"expected 1 CORS rule, got {res.Configuration.Rules.Count}");
            var r = res.Configuration.Rules[0];
            if (!r.AllowedOrigins.Contains("http://localhost:3000"))
                throw new InvalidOperationException("expected AllowedOrigins to contain http://localhost:3000");
            if (r.MaxAgeSeconds != 3600)
                throw new InvalidOperationException($"expected MaxAgeSeconds 3600, got {r.MaxAgeSeconds}");
        });
    }

    private static async Task TestHttpCorsPreflight(HttpClient http)
    {
        await Step("HttpCorsPreflight_AllowedOrigin", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, $"/{Bucket}/{Key}");
            req.Headers.Add("Origin", "http://localhost:3000");
            req.Headers.Add("Access-Control-Request-Method", "PUT");
            req.Headers.Add("Access-Control-Request-Headers", "content-type, x-amz-date");

            using var resp = await http.SendAsync(req);
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"expected 200 OK for preflight, got {(int)resp.StatusCode}");

            if (!resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) || !origins.Contains("http://localhost:3000"))
                throw new InvalidOperationException("missing or incorrect Access-Control-Allow-Origin header");

            if (!resp.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods) || !methods.Any(m => m.Contains("PUT")))
                throw new InvalidOperationException("missing or incorrect Access-Control-Allow-Methods header");

            if (!resp.Headers.TryGetValues("Access-Control-Max-Age", out var maxAge) || !maxAge.Contains("3600"))
                throw new InvalidOperationException("missing or incorrect Access-Control-Max-Age header");
        });

        await Step("HttpCorsPreflight_ForbiddenOrigin", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, $"/{Bucket}/{Key}");
            req.Headers.Add("Origin", "http://evil-attacker.com");
            req.Headers.Add("Access-Control-Request-Method", "PUT");

            using var resp = await http.SendAsync(req);
            if (resp.StatusCode != HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"expected 403 Forbidden for disallowed origin, got {(int)resp.StatusCode}");
        });
    }

    private static async Task TestHttpCorsActualRequest(HttpClient http)
    {
        await Step("HttpCorsActualRequest_HeadersInjected", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{Bucket}/{Key}");
            req.Headers.Add("Origin", "http://localhost:3000");

            using var resp = await http.SendAsync(req);
            if (resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins)
                && !origins.Contains("http://localhost:3000"))
            {
                throw new InvalidOperationException("incorrect Access-Control-Allow-Origin");
            }
        });
    }

    private static async Task TestPublicReadAndPrivateAccess(AmazonS3Client s3, HttpClient http)
    {
        await Step("PutBucketAcl_PublicRead", async () =>
        {
            await s3.PutACLAsync(new PutACLRequest
            {
                BucketName = Bucket,
                CannedACL = S3CannedACL.PublicRead,
            });
        });

        await Step("AnonymousRead_SucceedsOnPublicReadBucket", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{Bucket}/{Key}");
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"expected 200 OK for anonymous GET on public-read bucket, got {(int)resp.StatusCode}");

            var content = await resp.Content.ReadAsStringAsync();
            if (content != Body)
                throw new InvalidOperationException($"content mismatch: got '{content}', expected '{Body}'");
        });

        await Step("PutBucketAcl_Private", async () =>
        {
            await s3.PutACLAsync(new PutACLRequest
            {
                BucketName = Bucket,
                CannedACL = S3CannedACL.Private,
            });
        });

        await Step("AnonymousRead_FailsOnPrivateBucket", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{Bucket}/{Key}");
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode != HttpStatusCode.Forbidden && resp.StatusCode != HttpStatusCode.BadRequest)
                throw new InvalidOperationException($"expected 403 or 400 for anonymous GET on private bucket, got {(int)resp.StatusCode}");
        });
    }

    private static async Task TestDeleteCors(AmazonS3Client s3)
    {
        await Step("DeleteBucketCors", async () =>
        {
            await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = Bucket });
            try
            {
                var res = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = Bucket });
                if (res.HttpStatusCode != HttpStatusCode.NotFound && res.Configuration is { Rules.Count: > 0 })
                {
                    throw new InvalidOperationException($"expected 404 or empty CORS configuration after delete, got {(int)res.HttpStatusCode}");
                }
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        });
    }

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"  -> {name,-42} ");
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
}
