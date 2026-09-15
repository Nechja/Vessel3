#if VESSEL3_UI
using System.Text.Json.Serialization;

namespace Vessel3.Server.Ui;

internal sealed record UiOidc(string Issuer, string ClientId, string? AuthorizationEndpoint, string? TokenEndpoint, string? EndSessionEndpoint);

internal sealed record UiConfig(string AccessKey, string SecretKey, string Region, UiOidc? Oidc);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiConfig))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
#endif
