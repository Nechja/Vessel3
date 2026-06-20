#if VESSEL3_UI
using System.Text.Json.Serialization;

namespace Vessel3.Server.Ui;

internal sealed record UiConfig(string AccessKey, string SecretKey, string Region);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiConfig))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
#endif
