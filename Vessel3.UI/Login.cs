using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Vessel3.UI;

internal sealed class LoginException(string message) : Exception(message);

internal sealed class Login(IJSRuntime js, NavigationManager nav, UiOidc? oidc)
{
    private const string SessionKey = "vessel3.session";
    private const string FlowKey = "vessel3.login";
    private const string SignedOutKey = "vessel3.signedout";
    private const int SessionSeconds = 43200;
    private static readonly XNamespace Sts = "https://sts.amazonaws.com/doc/2011-06-15/";
    private readonly HttpClient http = new();
    private bool redirecting;
    private string? pendingReturn;

    private sealed record Flow(string Verifier, string State, string Nonce, string ReturnTo);

    private string RedirectUri => nav.BaseUri;
    private string Origin => new Uri(nav.BaseUri).GetLeftPart(UriPartial.Authority);

    public async Task<UiSession?> Restore()
    {
        var raw = await js.InvokeAsync<string?>("sessionStorage.getItem", SessionKey);
        if (raw is null) return null;
        if (Parse<UiSession>(raw) is { } session && session.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        return null;
    }

    public async Task<bool> SignedOut() =>
        await js.InvokeAsync<string?>("sessionStorage.getItem", SignedOutKey) is not null;

    public async Task<UiSession?> Complete()
    {
        var query = Query(nav.Uri);
        if (!query.ContainsKey("code") && !query.ContainsKey("error")) return null;

        var rawFlow = await js.InvokeAsync<string?>("sessionStorage.getItem", FlowKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", FlowKey);
        var flow = rawFlow is null ? null : Parse<Flow>(rawFlow);
        pendingReturn = flow?.ReturnTo ?? "";

        var stateMatches = flow is not null && query.GetValueOrDefault("state") == flow.State;
        if (query.TryGetValue("error", out var error))
            throw new LoginException(stateMatches ? query.GetValueOrDefault("error_description") ?? error : "sign-in refused");
        if (flow is null || !stateMatches) throw new LoginException("login state mismatch");

        var idToken = await IdToken(query["code"], flow.Verifier);
        var claims = Payload(idToken);
        if (Str(claims, "nonce") != flow.Nonce) throw new LoginException("login nonce mismatch");
        var subject = Str(claims, "preferred_username") ?? Str(claims, "email") ?? Str(claims, "sub") ?? "?";

        var session = await Exchange(idToken, subject);
        await js.InvokeVoidAsync("sessionStorage.setItem", SessionKey, JsonSerializer.Serialize(session));
        return session;
    }

    public async Task Begin(bool prompt = false)
    {
        if (oidc?.AuthorizationEndpoint is null) throw new LoginException("identity provider is not configured");
        if (redirecting) return;
        redirecting = true;

        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", SignedOutKey);

        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var returnTo = nav.ToBaseRelativePath(nav.Uri).Split('?')[0];
        await js.InvokeVoidAsync("sessionStorage.setItem", FlowKey, JsonSerializer.Serialize(new Flow(verifier, state, nonce, returnTo)));

        var url = oidc.AuthorizationEndpoint
            + "?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(oidc.ClientId)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=" + Uri.EscapeDataString("openid profile email")
            + "&state=" + state
            + "&nonce=" + nonce
            + "&code_challenge=" + challenge
            + "&code_challenge_method=S256"
            + (prompt ? "&prompt=login" : "");
        await js.InvokeVoidAsync("location.assign", url);
    }

    public async Task SignOut(UiSession session)
    {
        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        await js.InvokeVoidAsync("sessionStorage.setItem", SignedOutKey, "1");
        var target = oidc?.EndSessionEndpoint is { } end
            ? end + "?id_token_hint=" + Uri.EscapeDataString(session.IdToken) + "&post_logout_redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            : RedirectUri;
        await js.InvokeVoidAsync("location.assign", target);
    }

    public void Retry() => nav.NavigateTo(RedirectUri, forceLoad: true);

    public void ApplyPendingReturn()
    {
        if (pendingReturn is not { } target) return;
        pendingReturn = null;
        nav.NavigateTo(target, replace: true);
    }

    private async Task<string> IdToken(string code, string verifier)
    {
        using var resp = await http.PostAsync(oidc!.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = oidc.ClientId,
            ["code_verifier"] = verifier,
        }));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new LoginException($"token endpoint {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return Str(doc.RootElement, "id_token") ?? throw new LoginException("token endpoint returned no id_token");
    }

    private async Task<UiSession> Exchange(string idToken, string subject)
    {
        using var resp = await http.PostAsync(Origin + "/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Action"] = "AssumeRoleWithWebIdentity",
            ["WebIdentityToken"] = idToken,
            ["DurationSeconds"] = SessionSeconds.ToString(),
        }));
        if (resp.Content.Headers.ContentType?.MediaType is not ("text/xml" or "application/xml"))
            throw new LoginException($"exchange {(int)resp.StatusCode}");
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.Root is null) throw new LoginException("empty exchange response");
        if (doc.Root.Name.LocalName == "ErrorResponse")
        {
            var err = doc.Root.Element(Sts + "Error");
            throw new LoginException($"{err?.Element(Sts + "Code")?.Value}: {err?.Element(Sts + "Message")?.Value}");
        }
        var creds = doc.Root.Element(Sts + "AssumeRoleWithWebIdentityResult")?.Element(Sts + "Credentials")
            ?? throw new LoginException("exchange response had no credentials");
        return new UiSession(
            creds.Element(Sts + "AccessKeyId")!.Value,
            creds.Element(Sts + "SecretAccessKey")!.Value,
            creds.Element(Sts + "SessionToken")!.Value,
            DateTimeOffset.Parse(creds.Element(Sts + "Expiration")!.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal),
            subject,
            idToken);
    }

    private static T? Parse<T>(string raw) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Payload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new LoginException("id_token is not a JWT");
        return JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, string> Query(string uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = new Uri(uri).Query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[k] = v;
        }
        return result;
    }
}
