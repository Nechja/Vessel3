namespace Vessel3.Storage;

internal sealed class NullBlobLocationCatalog : IBlobLocationCatalog
{
    public static NullBlobLocationCatalog Instance { get; } = new();

    public string? LocateBlob(string sha) => null;

    public void RecordLocation(string sha, string volumeId)
    {
    }

    public void RemoveLocation(string sha)
    {
    }
}
