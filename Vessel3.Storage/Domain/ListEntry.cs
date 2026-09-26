namespace Vessel3.Storage;

internal abstract record ListEntry(string Key)
{
    internal sealed record Contents(string Key, long Size, DateTimeOffset LastModified, string Etag) : ListEntry(Key);
    internal sealed record CommonPrefix(string Key) : ListEntry(Key);
}
