namespace Vessel3.Protocols.Native;

internal sealed record NativeAuthOptions(
    bool IsUnauthenticated = false,
    string? RootAccessKey = null,
    string? RootSecretKey = null);
