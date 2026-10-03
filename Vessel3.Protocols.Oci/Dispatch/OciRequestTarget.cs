namespace Vessel3.Protocols.Oci.Dispatch;

internal sealed record OciRequestTarget(
    OciOperationKind Operation,
    string? Repo = null,
    string? Reference = null,
    string? UploadId = null);
