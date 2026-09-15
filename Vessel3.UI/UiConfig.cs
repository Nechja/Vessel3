namespace Vessel3.UI;

internal sealed record UiOidc(string Issuer, string ClientId, string? AuthorizationEndpoint, string? TokenEndpoint, string? EndSessionEndpoint);

internal sealed record UiConfig(string AccessKey, string SecretKey, string Region, UiOidc? Oidc);
