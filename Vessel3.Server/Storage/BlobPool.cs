#pragma warning disable CA5350
using System.Security.Cryptography;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal sealed record StoredBlob(string Sha, string Md5, string Crc32, string Crc32C, string Sha1, long Size);
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
        var moved = false;

        try
        {
            long total;
            string sha;
            string md5;
            string crc32hex;
            string crc32chex;
            string sha1hex;

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
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var crc32 = new System.IO.Hashing.Crc32();
                var crc32c = new Vessel3.Server.Crc32C();
                var buf = new byte[81920];
                total = 0;
                int n;
                while ((n = await source.ReadAsync(buf.AsMemory(), ct)) > 0)
                {
                    var span = buf.AsSpan(0, n);
                    sha256.AppendData(buf, 0, n);
                    md5Hash.AppendData(buf, 0, n);
                    sha1.AppendData(buf, 0, n);
                    crc32.Append(span);
                    crc32c.Append(span);
                    await temp.WriteAsync(buf.AsMemory(0, n), ct);
                    total += n;
                }
                sha = Convert.ToHexStringLower(sha256.GetHashAndReset());
                md5 = Convert.ToHexStringLower(md5Hash.GetHashAndReset());
                sha1hex = Convert.ToHexStringLower(sha1.GetHashAndReset());
                crc32hex = ChecksumAlgorithms.CrcUInt32ToHex(crc32.GetCurrentHashAsUInt32());
                crc32chex = ChecksumAlgorithms.CrcUInt32ToHex(crc32c.GetCurrentHashAndReset());
                temp.Flush(flushToDisk: true);
            }

            var finalPath = PathFor(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
                moved = true;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                moved = true;
                TryDelete(tempPath);
            }

            return new StoredBlob(sha, md5, crc32hex, crc32chex, sha1hex, total);
        }
        catch (IOException ex) when (IsOutOfSpace(ex))
        {
            return new InsufficientStorageError(ex.Message);
        }
        finally
        {
            if (!moved) TryDelete(tempPath);
        }
    }

    private static bool IsOutOfSpace(IOException ex) =>
        ex.HResult is unchecked((int)0x80070027) or unchecked((int)0x80070070)
            || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disk is full", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
