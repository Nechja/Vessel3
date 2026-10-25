namespace Vessel3.Storage;

internal interface IBlobReader
{
    string VolumeId { get; }
    Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default);
    Task<bool> BlobExists(string sha, CancellationToken ct = default);
}
