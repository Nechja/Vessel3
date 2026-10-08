namespace Vessel3.Storage;

internal interface IVolumeStorage
{
    string VolumeId { get; }
    Task<Result<StoredBlob>> WriteStagedBlob(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct);
    Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default);
    Task<bool> BlobExists(string sha, CancellationToken ct = default);
    Task<Result<bool>> DeleteBlob(string sha, CancellationToken ct = default);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}
