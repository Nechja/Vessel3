using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class VesselHttpTransport(HttpClient http, VesselClientOptions options, bool disposeHttpClient = false) : IDisposable
{
    public HttpClient Http => http;
    public VesselClientOptions Options => options;

    public void Dispose()
    {
        if (disposeHttpClient)
        {
            http.Dispose();
        }
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(options.BearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue(VesselHeaders.AuthorizationSchemeBearer, options.BearerToken);
        }
        else if (!string.IsNullOrEmpty(options.AccessKey))
        {
            req.Headers.TryAddWithoutValidation(VesselHeaders.AccessKey, options.AccessKey);
            if (!string.IsNullOrEmpty(options.SecretKey))
            {
                req.Headers.TryAddWithoutValidation(VesselHeaders.SecretKey, options.SecretKey);
            }
        }
        return req;
    }

    public ByteArrayContent CreateJsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(VesselMediaTypes.Json) { CharSet = VesselMediaTypes.Utf8CharSet };
        return content;
    }

    public async Task<Result<T>> ReadJson<T>(HttpResponseMessage res, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            return await ReadError(res, ct);
        }

        var stream = await res.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
        return parsed is not null ? parsed : new HttpError("SerializationError", "Null response body", (int)res.StatusCode);
    }

    public async Task<Error> ReadError(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
            {
                return new HttpError("HttpError", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", (int)res.StatusCode);
            }

            var errDto = JsonSerializer.Deserialize(bytes, VesselJsonContext.Default.ErrorDto);
            if (errDto.Error is { Length: > 0 } code)
            {
                return new HttpError(code, errDto.Message ?? string.Empty, (int)res.StatusCode);
            }

            var ociErr = JsonSerializer.Deserialize(bytes, VesselJsonContext.Default.OciErrorsDto);
            if (ociErr.Errors is { Count: > 0 } errors)
            {
                var first = errors[0];
                return new HttpError(first.Code ?? "OciError", first.Message ?? string.Empty, (int)res.StatusCode);
            }
        }
        catch
        {
        }
        return new HttpError("HttpError", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", (int)res.StatusCode);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        http.SendAsync(req, ct);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption completionOption, CancellationToken ct) =>
        http.SendAsync(req, completionOption, ct);

    public static string EscapeKey(string key)
    {
        var segments = key.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join('/', segments);
    }
}
