namespace Vessel3.Storage;

internal interface ITrailingChecksumProvider
{
    ChecksumSet TrailingChecksums { get; }
}
