namespace Vessel3.Protocols.Azure.Dispatch;

internal enum AzureOperationKind
{
    ListContainers,
    GetServiceProperties,
    SetServiceProperties,
    GetAccountInfo,

    CreateContainer,
    GetContainerProperties,
    DeleteContainer,
    GetContainerMetadata,
    SetContainerMetadata,
    GetContainerAcl,
    SetContainerAcl,
    ListBlobs,

    PutBlob,
    GetBlob,
    HeadBlob,
    DeleteBlob,
    CopyBlob,
    GetBlobMetadata,
    SetBlobMetadata,
    GetBlobTags,
    PutBlobTags,

    PutBlock,
    PutBlockList,
    GetBlockList,

    Unknown,
}

internal sealed record AzureRequestTarget(
    AzureOperationKind Operation,
    string? Account,
    string? Container,
    string? Blob);
