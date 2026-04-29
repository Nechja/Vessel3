namespace Vessel3.Server;

internal sealed record StoredObject(FileStream Body, long Size, DateTimeOffset LastModified);

internal sealed record ObjectStat(long Size, DateTimeOffset LastModified);

internal sealed class FileObjectStore(string root, ListCursorStore cursors)
{
    public async Task<Result<long>> Put(string bucket, string key, Stream body, long? contentLength, CancellationToken ct)
    {
        if (!TryResolve(bucket, key, out var path))
            return new InvalidPathError($"{bucket}/{key}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous,
            PreallocationSize = contentLength ?? 0,
        });

        await body.CopyToAsync(fs, ct);
        return fs.Length;
    }

    public Result<StoredObject> Get(string bucket, string key)
    {
        if (!TryResolve(bucket, key, out var path))
            return new InvalidPathError($"{bucket}/{key}");

        if (!File.Exists(path))
            return new NotFoundError($"{bucket}/{key}");

        var info = new FileInfo(path);
        var fs = new FileStream(
            path,
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new StoredObject(fs, info.Length, info.LastWriteTimeUtc);
    }

    public Result<ObjectStat> Stat(string bucket, string key)
    {
        if (!TryResolve(bucket, key, out var path))
            return new InvalidPathError($"{bucket}/{key}");

        if (!File.Exists(path))
            return new NotFoundError($"{bucket}/{key}");

        var info = new FileInfo(path);
        return new ObjectStat(info.Length, info.LastWriteTimeUtc);
    }

    public Result<bool> Delete(string bucket, string key)
    {
        if (!TryResolve(bucket, key, out var path))
            return new InvalidPathError($"{bucket}/{key}");

        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    public IEnumerable<BucketInfo> ListBuckets()
    {
        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(rootFull).OrderBy(d => d, StringComparer.Ordinal))
        {
            yield return new BucketInfo(Path.GetFileName(dir), Directory.GetCreationTimeUtc(dir));
        }
    }

    public Result<ListPage> ListObjects(ListRequest req, string? continuationToken)
    {
        if (continuationToken is not null)
        {
            var resumed = cursors.Resume(continuationToken);
            if (resumed is Result<ListCursor>.Failure f) return f.Error;
            var cursor = ((Result<ListCursor>.Success)resumed).Value;
            return Page(cursor.Request, cursor.Snapshot, cursor.Position);
        }

        if (!TryResolveBucket(req.Bucket, out var bucketPath))
            return new InvalidPathError(req.Bucket);

        if (!Directory.Exists(bucketPath))
            return new NotFoundError(req.Bucket);

        var snapshot = WalkBucket(bucketPath, req.Prefix, req.Delimiter, req.StartAfter).ToList();
        return Page(req, snapshot, 0);
    }

    private Result<ListPage> Page(ListRequest req, IReadOnlyList<ListEntry> snapshot, int position)
    {
        var take = Math.Min(req.MaxKeys, snapshot.Count - position);
        var slice = new List<ListEntry>(take);
        for (var i = 0; i < take; i++) slice.Add(snapshot[position + i]);

        var newPosition = position + take;
        var truncated = newPosition < snapshot.Count;
        var token = truncated
            ? cursors.Save(new ListCursor(req, snapshot, newPosition, DateTimeOffset.UtcNow))
            : null;

        return new ListPage(slice, truncated, token, slice.Count);
    }

    private IEnumerable<ListEntry> WalkBucket(string bucketPath, string? prefix, string? delimiter, string? startAfter)
    {
        var prefixStr = prefix ?? string.Empty;
        var lastSlash = prefixStr.LastIndexOf('/');
        var prefixDir = lastSlash >= 0 ? prefixStr[..(lastSlash + 1)] : string.Empty;
        var prefixSuffix = lastSlash >= 0 ? prefixStr[(lastSlash + 1)..] : prefixStr;
        var walkDir = string.IsNullOrEmpty(prefixDir)
            ? bucketPath
            : Path.Combine(bucketPath, prefixDir.TrimEnd('/'));

        if (!Directory.Exists(walkDir)) yield break;

        if (delimiter == "/")
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(walkDir).OrderBy(e => e, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (!name.StartsWith(prefixSuffix, StringComparison.Ordinal)) continue;

                var key = prefixDir + name;
                if (Directory.Exists(entry))
                {
                    var prefixKey = key + "/";
                    if (startAfter is not null && string.CompareOrdinal(prefixKey, startAfter) <= 0) continue;
                    yield return new ListEntry.CommonPrefix(prefixKey);
                }
                else
                {
                    if (startAfter is not null && string.CompareOrdinal(key, startAfter) <= 0) continue;
                    var info = new FileInfo(entry);
                    yield return new ListEntry.Contents(key, info.Length, info.LastWriteTimeUtc);
                }
            }
            yield break;
        }

        var files = Directory.EnumerateFiles(walkDir, "*", SearchOption.AllDirectories)
            .Select(f => (path: f, key: Path.GetRelativePath(bucketPath, f).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(t => t.key.StartsWith(prefixStr, StringComparison.Ordinal))
            .Where(t => startAfter is null || string.CompareOrdinal(t.key, startAfter) > 0)
            .OrderBy(t => t.key, StringComparer.Ordinal);

        foreach (var (path, key) in files)
        {
            var info = new FileInfo(path);
            yield return new ListEntry.Contents(key, info.Length, info.LastWriteTimeUtc);
        }
    }

    public Result<bool> CreateBucket(string bucket)
    {
        if (!TryResolveBucket(bucket, out var path))
            return new InvalidPathError(bucket);

        if (Directory.Exists(path))
            return false;

        Directory.CreateDirectory(path);
        return true;
    }

    public Result<bool> DeleteBucket(string bucket)
    {
        if (!TryResolveBucket(bucket, out var path))
            return new InvalidPathError(bucket);

        if (!Directory.Exists(path))
            return new NotFoundError(bucket);

        if (Directory.EnumerateFileSystemEntries(path).Any())
            return new BucketNotEmptyError(bucket);

        Directory.Delete(path);
        return true;
    }

    public Result<bool> BucketExists(string bucket) =>
        TryResolveBucket(bucket, out var path)
            ? Directory.Exists(path)
            : new InvalidPathError(bucket);

    private bool TryResolveBucket(string bucket, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(bucket)) return false;
        if (bucket.Contains('/') || bucket.Contains("..", StringComparison.Ordinal)) return false;

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, bucket));
        var rel = Path.GetRelativePath(rootFull, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;

        path = full;
        return true;
    }

    private bool TryResolve(string bucket, string key, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(key)) return false;
        if (bucket.Contains('/') || bucket.Contains("..", StringComparison.Ordinal)) return false;

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, bucket, key));
        var rel = Path.GetRelativePath(rootFull, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;

        path = full;
        return true;
    }
}
