namespace Vessel3.Storage;

internal sealed record ListPage(
    IReadOnlyList<ListEntry> Entries,
    bool IsTruncated,
    string? NextContinuationToken,
    string? LastKey,
    int KeyCount);
