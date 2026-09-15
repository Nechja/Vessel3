using System.Text.Json;

namespace Vessel3.Server.Oidc;

internal sealed record DiscoveryDocument(string? AuthorizationEndpoint, string? TokenEndpoint, string? JwksUri, string? EndSessionEndpoint);

internal interface IOidcDiscovery
{
    Task<DiscoveryDocument?> Get(CancellationToken ct);
}

internal sealed class OidcDiscovery(OidcOptions options, HttpClient http, TimeProvider clock) : IOidcDiscovery, IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim fetchLock = new(1, 1);
    private DiscoveryDocument? cached;
    private DateTimeOffset fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;

    public async Task<DiscoveryDocument?> Get(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (cached is not null && now - fetchedAt < MaxAge) return cached;
        await fetchLock.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (cached is not null && now - fetchedAt < MaxAge) return cached;
            if (now - lastAttempt < RetryInterval) return cached;
            lastAttempt = now;
            using var doc = JsonDocument.Parse(await http.GetByteArrayAsync(options.DiscoveryUrl, ct));
            var root = doc.RootElement;
            cached = new DiscoveryDocument(
                Str(root, "authorization_endpoint"),
                Str(root, "token_endpoint"),
                Str(root, "jwks_uri"),
                Str(root, "end_session_endpoint"));
            fetchedAt = now;
            return cached;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return cached;
        }
        finally
        {
            fetchLock.Release();
        }
    }

    public void Dispose() => fetchLock.Dispose();

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
}
