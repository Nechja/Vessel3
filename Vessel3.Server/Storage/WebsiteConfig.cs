using System.Text.Json.Serialization;

namespace Vessel3.Server.Storage;

internal sealed record WebsiteConfig(string IndexDocument, string? ErrorDocument = null);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WebsiteConfig))]
internal sealed partial class WebsiteJsonContext : JsonSerializerContext;
