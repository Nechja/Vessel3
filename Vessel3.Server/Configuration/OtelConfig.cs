namespace Vessel3.Server.Configuration;

public sealed record OtelConfig(
    bool Enabled,
    string? Endpoint,
    string ServiceName);
