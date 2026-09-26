using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal interface IObjectXmlReader
{
    Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct);
    Task<Result<IReadOnlyList<CompletedPart>>> ReadCompleteMultipartUploadRequest(Stream input, CancellationToken ct);
    Task<Result<IReadOnlyDictionary<string, string>>> ReadTagging(Stream input, CancellationToken ct);
    Task<Result<Retention>> ReadRetention(Stream input, CancellationToken ct);
    Task<Result<bool>> ReadLegalHold(Stream input, CancellationToken ct);
}
