using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Vessel3.UI;

internal sealed class SessionGuardHandler(UiAuth auth, Login login) : DelegatingHandler
{
    private static readonly string[] SessionLost = ["<Code>ExpiredToken</Code>", "<Code>InvalidToken</Code>", "<Code>InvalidAccessKeyId</Code>"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken);
        if (auth.Session is not null
            && resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
            && resp.Content.Headers.ContentType?.MediaType is "application/xml")
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (SessionLost.Any(body.Contains)) await login.Begin();
        }
        return resp;
    }
}

internal sealed class SigV4Handler(UiAuth auth, string region) : DelegatingHandler
{
    private const string Payload = "UNSIGNED-PAYLOAD";
    private const string SignedHeaders = "host;x-amz-content-sha256;x-amz-date;x-amz-security-token";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (auth.Session is { } session && request.RequestUri is { } uri && !uri.Query.Contains("X-Amz-Signature", StringComparison.Ordinal))
            Sign(request, uri, session);
        return base.SendAsync(request, cancellationToken);
    }

    private void Sign(HttpRequestMessage request, Uri uri, UiSession session)
    {
        var amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Remove("x-amz-security-token");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", Payload);
        request.Headers.TryAddWithoutValidation("x-amz-security-token", session.Token);

        var canonical = new StringBuilder()
            .Append(request.Method.Method).Append('\n')
            .Append(CanonicalPath(uri.AbsolutePath)).Append('\n')
            .Append(CanonicalQuery(uri.Query)).Append('\n')
            .Append("host:").Append(uri.Authority).Append('\n')
            .Append("x-amz-content-sha256:").Append(Payload).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n')
            .Append("x-amz-security-token:").Append(session.Token).Append('\n')
            .Append('\n')
            .Append(SignedHeaders).Append('\n')
            .Append(Payload)
            .ToString();

        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + session.Secret), date), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));
        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={session.AccessKey}/{scope}, SignedHeaders={SignedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string CanonicalPath(string escapedPath) =>
        string.Join('/', escapedPath.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment))));

    private static string CanonicalQuery(string query)
    {
        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                var k = eq < 0 ? pair : pair[..eq];
                var v = eq < 0 ? "" : pair[(eq + 1)..];
                return (K: Encode(Decode(k)), V: Encode(Decode(v)));
            })
            .OrderBy(p => p.K, StringComparer.Ordinal)
            .ThenBy(p => p.V, StringComparer.Ordinal);
        return string.Join('&', pairs.Select(p => $"{p.K}={p.V}"));
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
