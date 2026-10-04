namespace Vessel3.Server.S3;

internal sealed class S3XmlReader(
    IBucketXmlReader bucketReader,
    IObjectXmlReader objectReader) : IS3XmlReader
{
    public S3XmlReader() : this(new BucketXmlReader(), new ObjectXmlReader())
    {
    }

    public Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct) =>
        bucketReader.ReadVersioningConfiguration(input, ct);

    public Task<Result<ObjectLockConfig>> ReadObjectLockConfiguration(Stream input, CancellationToken ct) =>
        bucketReader.ReadObjectLockConfiguration(input, ct);

    public Task<Result<LifecycleConfig>> ReadLifecycleConfiguration(Stream input, CancellationToken ct) =>
        bucketReader.ReadLifecycleConfiguration(input, ct);

    public Task<Result<WebsiteConfig>> ReadWebsiteConfiguration(Stream input, CancellationToken ct) =>
        bucketReader.ReadWebsiteConfiguration(input, ct);

    public Task<Result<CorsConfig>> ReadCorsConfiguration(Stream input, CancellationToken ct) =>
        bucketReader.ReadCorsConfiguration(input, ct);

    public Task<Result<bool>> ReadAccessControlPolicy(Stream input, CancellationToken ct) =>
        bucketReader.ReadAccessControlPolicy(input, ct);

    public Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct) =>
        objectReader.ReadBatchDeleteRequest(input, ct);

    public Task<Result<IReadOnlyList<CompletedPart>>> ReadCompleteMultipartUploadRequest(Stream input, CancellationToken ct) =>
        objectReader.ReadCompleteMultipartUploadRequest(input, ct);

    public Task<Result<IReadOnlyDictionary<string, string>>> ReadTagging(Stream input, CancellationToken ct) =>
        objectReader.ReadTagging(input, ct);

    public Task<Result<Retention>> ReadRetention(Stream input, CancellationToken ct) =>
        objectReader.ReadRetention(input, ct);

    public Task<Result<bool>> ReadLegalHold(Stream input, CancellationToken ct) =>
        objectReader.ReadLegalHold(input, ct);
}
