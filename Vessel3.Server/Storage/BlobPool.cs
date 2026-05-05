using System.Security.Cryptography;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal sealed record StoredBlob(string Sha, string Md5, long Size);
internal sealed record BlobPoolOptions(string Root);

internal interface IBlobPool
{
    Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, CancellationToken ct);
    Result<Stream> Open(string sha);
    bool Exists(string sha);
    Result<bool> Delete(string sha);
    IEnumerable<string> EnumerateAll();
    DateTime? GetLastWriteUtc(string sha);
}

internal sealed class BlobPool(BlobPoolOptions options) : IBlobPool
{
    public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, CancellationToken ct)
    {
        var tmpDir = Path.Combine(options.Root, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tempPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));

        long total;
        string sha;
        string md5;

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
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5Hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buf = new byte[81920];
            total = 0;
            int n;
            while ((n = await source.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                sha256.AppendData(buf, 0, n);
                md5Hash.AppendData(buf, 0, n);
                await temp.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
            }
            sha = Convert.ToHexStringLower(sha256.GetHashAndReset());
            md5 = Convert.ToHexStringLower(md5Hash.GetHashAndReset());
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

        return new StoredBlob(sha, md5, total);
    }

    public Result<Stream> Open(string sha)
    {
        var path = PathFor(sha);
        return File.Exists(path)
            ? new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            })
            : (Result<Stream>)new NotFoundError($"blob {sha}");
    }

    public bool Exists(string sha) => File.Exists(PathFor(sha));

    public DateTime? GetLastWriteUtc(string sha)
    {
        var path = PathFor(sha);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public Result<bool> Delete(string sha)
    {
        var path = PathFor(sha);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public IEnumerable<string> EnumerateAll()
    {
        var rootFull = Path.GetFullPath(options.Root);
        if (!Directory.Exists(rootFull)) yield break;
        foreach (var path in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (IsLikelySha(name)) yield return name;
        }
    }

    private string PathFor(string sha) =>
        Path.Combine(options.Root, sha[..2], sha[2..4], sha);

    private bool IsLikelySha(string name) =>
        name.Length == 64 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
