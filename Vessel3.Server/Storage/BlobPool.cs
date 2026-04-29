using System.Security.Cryptography;

namespace Vessel3.Server.Storage;

internal sealed record StoredBlob(string Sha, long Size);

// Content-addressable blob pool. Layout: <root>/blobs/<aa>/<bb>/<sha>.
// Writes are atomic via temp-file + rename. Identical content dedups for free.
internal sealed class BlobPool(string root)
{
    public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, CancellationToken ct)
    {
        var tmpDir = Path.Combine(root, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tempPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));

        long total;
        string sha;

        await using (var temp = new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous,
            PreallocationSize = declaredSize ?? 0,
        }))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            total = 0;
            int n;
            while ((n = await source.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await temp.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
            }
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        var finalPath = PathFor(sha);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        try
        {
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            File.Delete(tempPath);
        }

        return new StoredBlob(sha, total);
    }

    public Result<FileStream> Open(string sha)
    {
        var path = PathFor(sha);
        if (!File.Exists(path))
            return new NotFoundError($"blob {sha}");

        var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        return fs;
    }

    public bool Exists(string sha) => File.Exists(PathFor(sha));

    public Result<bool> Delete(string sha)
    {
        var path = PathFor(sha);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public IEnumerable<string> EnumerateAll()
    {
        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull)) yield break;
        foreach (var path in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (IsLikelySha(name)) yield return name;
        }
    }

    private string PathFor(string sha) =>
        Path.Combine(root, sha[..2], sha[2..4], sha);

    private bool IsLikelySha(string name) =>
        name.Length == 64 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
