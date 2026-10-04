namespace Vessel3.Storage;

internal interface IVolumeStorage
{
    string VolumeId { get; }
    Task<Result<StoredBlob>> WriteStagedBlobAsync(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct);
    Task<Result<Stream>> OpenBlobAsync(string sha, CancellationToken ct = default);
    Task<bool> BlobExistsAsync(string sha, CancellationToken ct = default);
    Task<Result<bool>> DeleteBlobAsync(string sha, CancellationToken ct = default);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}
