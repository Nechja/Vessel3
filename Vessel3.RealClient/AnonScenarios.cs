using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

internal static class AnonScenarios
{
    private static readonly string[] InvalidBucketNames =
        ["UPPERCASE", "ab", "with_underscore", "-leading", "trailing-"];

    public static async Task<int> RunAll(string endpoint)
    {
        using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };

        await Run("CreateBucket", async () =>
        {
            using var resp = await http.PutAsync("vessel3-anon", new StringContent(""));
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"create bucket failed: {(int)resp.StatusCode}");
        });

        await Run("ChunkedUpload", async () =>
        {
            var payload = Encoding.UTF8.GetBytes("hello chunked world");
            var body = new MemoryStream();
            var header = Encoding.ASCII.GetBytes($"{payload.Length:x}\r\n");
            body.Write(header);
            body.Write(payload);
            body.Write("\r\n0\r\n\r\n"u8);
            body.Position = 0;

            using var req = new HttpRequestMessage(HttpMethod.Put, "vessel3-anon/chunked.txt");
            req.Content = new StreamContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content.Headers.ContentEncoding.Add("aws-chunked");
            req.Headers.Add("x-amz-decoded-content-length", payload.Length.ToString(CultureInfo.InvariantCulture));
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"chunked PUT failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

            using var got = await http.GetAsync("vessel3-anon/chunked.txt");
            var body2 = await got.Content.ReadAsByteArrayAsync();
            if (!body2.AsSpan().SequenceEqual(payload))
                throw new InvalidOperationException($"chunked body roundtrip mismatch: got '{Encoding.UTF8.GetString(body2)}'");
        });

        await Run("InvalidBucketName", async () =>
        {
            foreach (var bad in InvalidBucketNames)
            {
                using var resp = await http.PutAsync(bad, new StringContent(""));
                if ((int)resp.StatusCode != 400)
                    throw new InvalidOperationException($"bucket '{bad}' got {(int)resp.StatusCode}, expected 400");
            }
        });

        await Run("MalformedVersioningXml", async () =>
        {
            var body = new StringContent(
                "<VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>",
                Encoding.UTF8, "application/xml");
            using var resp = await http.PutAsync("vessel3-anon?versioning", body);
            if ((int)resp.StatusCode != 400)
                throw new InvalidOperationException($"missing-Status versioning got {(int)resp.StatusCode}, expected 400");
        });

        await Run("Cleanup", async () =>
        {
            using var del = await http.DeleteAsync("vessel3-anon/chunked.txt");
            del.EnsureSuccessStatusCode();
            using var delBucket = await http.DeleteAsync("vessel3-anon");
            delBucket.EnsureSuccessStatusCode();
        });

        Console.WriteLine();
        Console.WriteLine("ANON OK");
        return 0;
    }

    private static async Task Run(string name, Func<Task> action)
    {
        Console.Write($"==> {name,-22} ");
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
