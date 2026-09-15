using System.Text.Json;

namespace Vessel3.Server.Oidc;

internal sealed record DiscoveryDocument(string? AuthorizationEndpoint, string? TokenEndpoint, string? JwksUri, string? EndSessionEndpoint);

internal interface IOidcDiscovery
{
    Task<DiscoveryDocument?> Get(CancellationToken ct);
}

internal sealed class OidcDiscovery(OidcOptions options, HttpClient http, TimeProvider clock) : IOidcDiscovery
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    private readonly Lock gate = new();
    private DiscoveryDocument? cached;
    private DateTimeOffset refreshAfter = DateTimeOffset.MinValue;
    private Task<DiscoveryDocument?>? inflight;

    public Task<DiscoveryDocument?> Get(CancellationToken ct)
    {
        lock (gate)
        {
            if (clock.GetUtcNow() < refreshAfter) return Task.FromResult(cached);
            inflight = inflight is { IsCompleted: false } running ? running : Fetch();
            return inflight.WaitAsync(ct);
        }
    }

    private async Task<DiscoveryDocument?> Fetch()
    {
        DiscoveryDocument? fetched = null;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetByteArrayAsync(options.DiscoveryUrl, CancellationToken.None));
            var root = doc.RootElement;
            fetched = new DiscoveryDocument(
                Str(root, "authorization_endpoint"),
                Str(root, "token_endpoint"),
                Str(root, "jwks_uri"),
                Str(root, "end_session_endpoint"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
        }

        lock (gate)
        {
            if (fetched is not null) cached = fetched;
            refreshAfter = clock.GetUtcNow() + (fetched is null ? RetryInterval : MaxAge);
            return cached;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
}
