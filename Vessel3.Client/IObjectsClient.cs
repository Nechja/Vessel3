using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IObjectsClient
{
    Task<Result<ObjectsPageDto>> ListObjects(string bucket, string? prefix = null, string? delimiter = null, string? marker = null, int limit = 1000, CancellationToken ct = default);
    Task<Result<VesselObjectDownload>> GetObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<ObjectSummaryDto>> StatObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> PutObject(string bucket, string key, Stream content, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> PutObject(string bucket, string key, Stream content, string? contentType, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress, CancellationToken ct = default);
    Task<Result<CreateUploadResultDto>> CreateMultipartUpload(string bucket, string key, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task<Result<UploadPartDto>> UploadPart(string bucket, string key, string uploadId, int partNumber, Stream content, long length, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> CompleteMultipartUpload(string bucket, string key, string uploadId, IReadOnlyList<UploadPartDto> parts, CancellationToken ct = default);
    Task<Result> AbortMultipartUpload(string bucket, string key, string uploadId, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> UploadObject(string bucket, string key, Stream content, long size, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, IProgress<long>? progress = null, CancellationToken ct = default);
    Task<Result> DeleteObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);
}
