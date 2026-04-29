namespace Vessel3.Server;

internal sealed record StoredObject(FileStream Body, long Size, DateTimeOffset LastModified);

internal sealed class FileObjectStore(string root)
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
