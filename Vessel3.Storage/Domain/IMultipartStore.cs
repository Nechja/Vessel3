namespace Vessel3.Storage;

internal interface IMultipartStore
{
    Result<CreateUploadOutcome> Create(string bucket, string key, string? contentType, IReadOnlyDictionary<string, string> metadata);
    Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, DeclaredChecksums declaredChecksums, CancellationToken ct);
    Task<Result<UploadPartOutcome>> UploadPart(string uploadId, int partNumber, Stream body, long? declaredSize, ChecksumSet declaredChecksums, CancellationToken ct);
    Task<Result<CompleteUploadOutcome>> Complete(string uploadId, IReadOnlyList<CompletedPart> clientParts, ChecksumAlgorithm? compositeAlgo, CancellationToken ct);
    Result Abort(string uploadId);
    IEnumerable<InProgressUpload> ListUploads(string bucket);
    Result<IReadOnlyList<ListedPart>> ListParts(string uploadId);
    IEnumerable<string> EnumerateInFlightPartShas();
    int ReapAbandonedUploads(DateTime cutoffUtc);
}
