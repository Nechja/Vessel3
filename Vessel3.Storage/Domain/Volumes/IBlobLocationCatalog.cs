namespace Vessel3.Storage;

internal interface IBlobLocationCatalog
{
    string? LocateBlob(string sha);
    void RecordLocation(string sha, string volumeId);
    void RemoveLocation(string sha);
}
