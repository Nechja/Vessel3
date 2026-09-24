using System.Globalization;
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

internal static class WebsiteScenarios
{
    private const string Bucket = "vessel3-webhost-probe";
    private const string IndexBody = "<!DOCTYPE html><html><body><h1>Welcome to Vessel3 Website</h1></body></html>";
    private const string DocsIndexBody = "<!DOCTYPE html><html><body><h1>Documentation Home</h1></body></html>";
    private const string DocsIntroBody = "<!DOCTYPE html><html><body><h1>Intro Guide</h1></body></html>";
    private const string CssBody = "body { font-family: sans-serif; }";
    private const string Error404Body = "<!DOCTYPE html><html><body><h1>Page Was Not Found</h1></body></html>";

    public static async Task<int> Run(AmazonS3Client s3, string endpoint)
    {
        Console.WriteLine("--- Website Hosting Scenarios ---");

        var baseUri = new Uri(endpoint);
        var hostPort = baseUri.IsDefaultPort ? "" : $":{baseUri.Port.ToString(CultureInfo.InvariantCulture)}";
        var customDomainHost = $"{Bucket}{hostPort}";

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };

        // Ensure clean initial state
        await DurabilityTester.CleanupBucket(s3, Bucket);

        try
        {
            await Step("CreateBucket", async () =>
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
            });

            await Step("GetWebsiteBeforeConfig", async () =>
            {
                try
                {
                    await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = Bucket });
                    throw new InvalidOperationException("expected 404 NoSuchWebsiteConfiguration before config is set");
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Expected: NoSuchWebsiteConfiguration
                }
            });

            await Step("SeedWebsiteObjects", async () =>
            {
                await PutObject(s3, "index.html", IndexBody, "text/html");
                await PutObject(s3, "styles/main.css", CssBody, "text/css");
                await PutObject(s3, "docs/index.html", DocsIndexBody, "text/html");
                await PutObject(s3, "docs/intro.html", DocsIntroBody, "text/html");
                await PutObject(s3, "404.html", Error404Body, "text/html");
            });

            await Step("PutBucketWebsite", async () =>
            {
                await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
                {
                    BucketName = Bucket,
                    WebsiteConfiguration = new WebsiteConfiguration
                    {
                        IndexDocumentSuffix = "index.html",
                        ErrorDocument = "404.html",
                    },
                });
            });

            await Step("GetBucketWebsite", async () =>
            {
                var resp = await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = Bucket });
                if (resp.WebsiteConfiguration is null)
                    throw new InvalidOperationException("WebsiteConfiguration was null");
                if (resp.WebsiteConfiguration.IndexDocumentSuffix != "index.html")
                    throw new InvalidOperationException($"IndexDocumentSuffix '{resp.WebsiteConfiguration.IndexDocumentSuffix}' != 'index.html'");
                if (resp.WebsiteConfiguration.ErrorDocument != "404.html")
                    throw new InvalidOperationException($"ErrorDocument '{resp.WebsiteConfiguration.ErrorDocument}' != '404.html'");
            });

            await Step("WebGet_RootIndex", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"expected 200 OK, got {(int)resp.StatusCode}");

                var mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"expected text/html, got '{mediaType}'");

                var body = await resp.Content.ReadAsStringAsync();
                if (!body.Contains("Welcome to Vessel3 Website", StringComparison.Ordinal))
                    throw new InvalidOperationException($"unexpected body: '{body}'");
            });

            await Step("WebHead_RootIndex", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, "/");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"expected 200 OK for HEAD, got {(int)resp.StatusCode}");

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                    throw new InvalidOperationException($"HEAD response must have zero body length, got {bytes.Length}");

                if (resp.Content.Headers.ContentLength != IndexBody.Length)
                    throw new InvalidOperationException($"HEAD Content-Length {resp.Content.Headers.ContentLength} != {IndexBody.Length}");
            });

            await Step("WebGet_StaticAsset", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/styles/main.css");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"expected 200 OK, got {(int)resp.StatusCode}");

                var mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, "text/css", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"expected text/css, got '{mediaType}'");

                var body = await resp.Content.ReadAsStringAsync();
                if (!body.Contains("font-family", StringComparison.Ordinal))
                    throw new InvalidOperationException($"unexpected body: '{body}'");
            });

            await Step("WebGet_FolderRedirect", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/docs");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.MovedPermanently)
                    throw new InvalidOperationException($"expected 301 MovedPermanently, got {(int)resp.StatusCode}");

                var loc = resp.Headers.Location?.ToString();
                if (loc != "/docs/")
                    throw new InvalidOperationException($"expected Location '/docs/', got '{loc}'");
            });

            await Step("WebGet_FolderIndex", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/docs/");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"expected 200 OK, got {(int)resp.StatusCode}");

                var body = await resp.Content.ReadAsStringAsync();
                if (!body.Contains("Documentation Home", StringComparison.Ordinal))
                    throw new InvalidOperationException($"unexpected body: '{body}'");
            });

            await Step("WebGet_FolderDirectKey", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/docs/intro.html");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"expected 200 OK, got {(int)resp.StatusCode}");

                var body = await resp.Content.ReadAsStringAsync();
                if (!body.Contains("Intro Guide", StringComparison.Ordinal))
                    throw new InvalidOperationException($"unexpected body: '{body}'");
            });

            await Step("WebGet_CustomErrorPage", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/non-existent-page.html");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                if (resp.StatusCode != HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"expected 404 NotFound, got {(int)resp.StatusCode}");

                var body = await resp.Content.ReadAsStringAsync();
                if (!body.Contains("Page Was Not Found", StringComparison.Ordinal))
                    throw new InvalidOperationException($"expected custom 404 body, got '{body}'");
            });

            var domainEnv = Environment.GetEnvironmentVariable("VESSEL3_DOMAIN");
            if (!string.IsNullOrEmpty(domainEnv) && domainEnv.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                await Step("WebGet_SubdomainRouting", async () =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "/");
                    req.Headers.Host = $"{Bucket}.localhost{hostPort}";
                    using var resp = await http.SendAsync(req);

                    if (resp.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException($"expected 200 OK via subdomain, got {(int)resp.StatusCode}");

                    var body = await resp.Content.ReadAsStringAsync();
                    if (!body.Contains("Welcome to Vessel3 Website", StringComparison.Ordinal))
                        throw new InvalidOperationException($"unexpected body via subdomain: '{body}'");
                });
            }

            await Step("DeleteBucketWebsite", async () =>
            {
                await s3.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = Bucket });

                try
                {
                    await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = Bucket });
                    throw new InvalidOperationException("expected 404 NoSuchWebsiteConfiguration after delete");
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Expected: config removed
                }
            });

            await Step("WebGet_DisabledAfterDelete", async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/");
                req.Headers.Host = customDomainHost;
                using var resp = await http.SendAsync(req);

                // With website configuration deleted, anonymous GET / must not serve the website index
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (body.Contains("Welcome to Vessel3 Website", StringComparison.Ordinal))
                        throw new InvalidOperationException("website content was still served after DeleteBucketWebsite");
                }
            });
        }
        finally
        {
            await DurabilityTester.CleanupBucket(s3, Bucket);
        }

        Console.WriteLine();
        Console.WriteLine("WEBSITE OK");
        return 0;
    }

    private static async Task PutObject(AmazonS3Client s3, string key, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream(bytes);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = ms,
            ContentType = contentType,
        });
    }

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"==> {name,-28} ");
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
