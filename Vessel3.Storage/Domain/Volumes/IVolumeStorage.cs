namespace Vessel3.Storage;

internal interface IVolumeStorage : IBlobReader
{
    Task<Result<StoredBlob>> WriteStagedBlob(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct);
    Task<Result<bool>> DeleteBlob(string sha, CancellationToken ct = default);
    IEnumerable<string> EnumerateShards();
    IEnumerable<string> Enumerate(string shard);
    DateTime? GetLastWriteUtc(string sha);
    int ReapAbandonedTempFiles(DateTime cutoffUtc);
}
