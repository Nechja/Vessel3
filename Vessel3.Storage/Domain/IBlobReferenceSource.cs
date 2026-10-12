namespace Vessel3.Storage;

internal interface IBlobReferenceSource
{
    string ProtocolName { get; }
    IAsyncEnumerable<string> AllReferencedBlobs(CancellationToken ct = default);
    IEnumerable<string> EnumerateInFlightShas() => [];
}
