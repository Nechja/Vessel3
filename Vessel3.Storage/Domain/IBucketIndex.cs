namespace Vessel3.Storage;

internal interface IBucketIndex : IDisposable
{
    void Open();
    BucketIndexTxScope BeginTransaction();
    long MaxSeq();
    void MarkApplied(long seq);
    long AppliedSeq();
    string? GetOwner();
    void SetOwner(string ownerId);
    void SnapshotTo(string path);
    void Insert(PutEvent ev);
    void Insert(DeleteMarkerEvent ev);
    void Remove(string key, string versionId);
    void UpdateTags(string key, string versionId, IReadOnlyDictionary<string, string> tags);
    void ApplyRetention(string key, string versionId, RetentionMode mode, long retainUntilUnixSeconds);
    void ApplyLegalHold(string key, string versionId, bool on);
    VersionKind? GetVersionKind(string key, string versionId);
    VersionKind? GetCurrentKind(string key);
    long VersionCount();
    int CountVersions(string key);
    Result<PutEntry> GetVersion(string key, string versionId);
    string? LatestVersionId(string key);
    string? GetCurrentPutVersionId(string key);
    Result<PutEntry> GetCurrentPut(string key);
    (List<AllVersionsEntry> Entries, bool IsTruncated) ListAllVersions(string? prefix, string? keyMarker, int limit);
    (List<VersionListEntry> Entries, bool IsTruncated) ListCurrent(string? prefix, KeyBound? from, int limit);
    bool IsEmpty();
    IAsyncEnumerable<string> ReferencedBlobs(CancellationToken ct = default);
    ObjectLockStatus GetObjectLockStatus(string key, string versionId);
}
