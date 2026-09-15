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
    private const int SessionSeconds = 43200;
    private static readonly XNamespace Sts = "https://sts.amazonaws.com/doc/2011-06-15/";
    private readonly HttpClient http = new();

    private sealed record Flow(string Verifier, string State, string Nonce, string ReturnTo);

    private string RedirectUri => nav.BaseUri;
    private string Origin => new Uri(nav.BaseUri).GetLeftPart(UriPartial.Authority);

    public async Task<UiSession?> Restore()
    {
        var raw = await js.InvokeAsync<string?>("sessionStorage.getItem", SessionKey);
        if (raw is null) return null;
        var session = JsonSerializer.Deserialize<UiSession>(raw);
        if (session is not null && session.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        return null;
    }

    public async Task<bool> Complete()
    {
        var query = Query(nav.Uri);
        if (!query.ContainsKey("code") && !query.ContainsKey("error")) return false;

        var rawFlow = await js.InvokeAsync<string?>("sessionStorage.getItem", FlowKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", FlowKey);
        var flow = rawFlow is null ? null : JsonSerializer.Deserialize<Flow>(rawFlow);
        if (flow is null || !query.TryGetValue("state", out var state) || state != flow.State)
            throw new LoginException("login state mismatch");
        if (query.TryGetValue("error", out var error))
            throw new LoginException(query.GetValueOrDefault("error_description") ?? error);

        var idToken = await IdToken(query["code"], flow.Verifier);
        var claims = Payload(idToken);
        if (Str(claims, "nonce") != flow.Nonce) throw new LoginException("login nonce mismatch");
        var subject = Str(claims, "preferred_username") ?? Str(claims, "email") ?? Str(claims, "sub") ?? "?";

        var session = await Exchange(idToken, subject);
        await js.InvokeVoidAsync("sessionStorage.setItem", SessionKey, JsonSerializer.Serialize(session));
        await js.InvokeVoidAsync("location.replace", RedirectUri + flow.ReturnTo);
        return true;
    }

    public async Task Begin()
    {
        if (oidc?.AuthorizationEndpoint is null) throw new LoginException("identity provider is not configured");
        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);

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
            + "&code_challenge_method=S256";
        await js.InvokeVoidAsync("location.assign", url);
    }

    public async Task SignOut(UiSession? session)
    {
        await js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        var target = oidc?.EndSessionEndpoint is { } end && session is not null
            ? end + "?id_token_hint=" + Uri.EscapeDataString(session.IdToken) + "&post_logout_redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            : RedirectUri;
        await js.InvokeVoidAsync("location.assign", target);
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
