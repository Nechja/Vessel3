namespace Vessel3.Storage;

internal interface IClusterReplicationTransport
{
    Task<Result<ClusterCatalogSnapshot>> FetchCatalog(string peerEndpoint, CancellationToken ct = default);
    Task<Result<BucketEventsBatch>> FetchBucketEvents(string peerEndpoint, string bucket, long afterSeq, CancellationToken ct = default);
    Task<Result<Stream>> OpenBucketSnapshot(string peerEndpoint, string bucket, CancellationToken ct = default);
    Task<Result<IdentityReplicationSnapshot>> FetchIamSnapshot(string peerEndpoint, CancellationToken ct = default);
    Task<Result<ClusterGcReferencesResponse>> FetchGcReferences(string peerEndpoint, CancellationToken ct = default);
    Task<Result<UploadSessionSnapshot>> FetchUploadSession(string peerEndpoint, string sessionId, CancellationToken ct = default);
    Task<bool> DeleteUploadSession(string peerEndpoint, string sessionId, CancellationToken ct = default);
}
