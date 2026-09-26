namespace Vessel3.Storage;

internal enum ChecksumState
{
    None,
    Trailing,
    Provided,
}

internal readonly record struct ChecksumTarget(ChecksumState State, string? Value)
{
    public static ChecksumTarget None => new(ChecksumState.None, null);
    public static ChecksumTarget Trailing => new(ChecksumState.Trailing, null);
    public static ChecksumTarget Provided(string hex) => new(ChecksumState.Provided, hex);

    public bool IsTrailing => State is ChecksumState.Trailing;
    public bool IsProvided => State is ChecksumState.Provided;
    public bool HasExpectation => State is not ChecksumState.None;
}

internal sealed record DeclaredChecksums(
    ChecksumTarget Crc32,
    ChecksumTarget Crc32C,
    ChecksumTarget Sha1,
    ChecksumTarget Sha256)
{
    public static DeclaredChecksums Empty { get; } = new(
        ChecksumTarget.None, ChecksumTarget.None, ChecksumTarget.None, ChecksumTarget.None);

    public bool HasAny => Crc32.HasExpectation || Crc32C.HasExpectation || Sha1.HasExpectation || Sha256.HasExpectation;

    public ChecksumIntent ToIntent() => new(Crc32.HasExpectation, Crc32C.HasExpectation, Sha1.HasExpectation);

    public static DeclaredChecksums FromSet(ChecksumSet set) => new(
        set.Crc32 is not null ? ChecksumTarget.Provided(set.Crc32) : ChecksumTarget.None,
        set.Crc32C is not null ? ChecksumTarget.Provided(set.Crc32C) : ChecksumTarget.None,
        set.Sha1 is not null ? ChecksumTarget.Provided(set.Sha1) : ChecksumTarget.None,
        set.Sha256 is not null ? ChecksumTarget.Provided(set.Sha256) : ChecksumTarget.None);
}
