namespace Vessel3.Storage;

internal sealed record ChecksumSet(string? Crc32, string? Crc32C, string? Sha1, string? Sha256)
{
    public static ChecksumSet Empty { get; } = new(null, null, null, null);

    public string? Get(ChecksumAlgorithm algo) => algo switch
    {
        ChecksumAlgorithm.Crc32  => Crc32,
        ChecksumAlgorithm.Crc32C => Crc32C,
        ChecksumAlgorithm.Sha1   => Sha1,
        ChecksumAlgorithm.Sha256 => Sha256,
        _ => null,
    };

    public static implicit operator DeclaredChecksums(ChecksumSet set) => DeclaredChecksums.FromSet(set);
}
