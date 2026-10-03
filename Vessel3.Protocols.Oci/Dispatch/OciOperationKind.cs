namespace Vessel3.Protocols.Oci.Dispatch;

internal enum OciOperationKind
{
    Unknown,
    Ping,
    Token,
    Catalog,
    Tags,
    GetBlob,
    HeadBlob,
    StartBlobUpload,
    AppendBlobUploadChunk,
    CommitBlobUpload,
    GetBlobUploadStatus,
    CancelBlobUpload,
    GetManifest,
    HeadManifest,
    PutManifest,
    DeleteManifest
}
