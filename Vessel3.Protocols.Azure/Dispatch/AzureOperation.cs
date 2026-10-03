namespace Vessel3.Protocols.Azure.Dispatch;

internal enum AzureOperationKind
{
    // Service level
    ListContainers,
    GetServiceProperties,
    SetServiceProperties,
    GetAccountInfo,

    // Container level
    CreateContainer,
    GetContainerProperties,
    DeleteContainer,
    GetContainerMetadata,
    SetContainerMetadata,
    GetContainerAcl,
    SetContainerAcl,
    ListBlobs,

    // Blob level
    PutBlob,
    GetBlob,
    HeadBlob,
    DeleteBlob,
    CopyBlob,
    GetBlobMetadata,
    SetBlobMetadata,
    GetBlobTags,
    PutBlobTags,

    // Staged Block level
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
