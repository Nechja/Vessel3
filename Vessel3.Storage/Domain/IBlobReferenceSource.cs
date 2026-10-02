namespace Vessel3.Storage;

internal interface IBlobReferenceSource
{
    string ProtocolName { get; }
    IEnumerable<string> AllReferencedBlobs();
    IEnumerable<string> EnumerateInFlightShas() => [];
}
