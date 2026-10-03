namespace Vessel3.Protocols.WebDav.Dispatch;

public sealed record WebDavRequestTarget(
    WebDavOperationKind Operation,
    string? Bucket,
    string? Path,
    int Depth,
    string? Destination = null,
    bool Overwrite = true,
    bool IsCollection = false);
