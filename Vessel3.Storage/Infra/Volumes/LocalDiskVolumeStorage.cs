#pragma warning disable CA5350
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Vessel3.Storage;

internal sealed class LocalDiskVolumeStorage(StorageVolume volume, IFileSync fileSync) : IVolumeStorage
{
    private readonly ConcurrentDictionary<string, bool> ensuredDirs = new();
    private bool tmpDirEnsured;

    public string VolumeId => volume.Id;

    public async Task<Result<StoredBlob>> WriteStagedBlob(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct)
    {
        if (!tmpDirEnsured)
        {
            Directory.CreateDirectory(volume.TmpDir);
            tmpDirEnsured = true;
        }

        var tempPath = Path.Combine(volume.TmpDir, Guid.NewGuid().ToString("N"));
        var moved = false;

        try
        {
            long total;
            string sha;
            string md5;
            string? crc32hex = null;
            string? crc32chex = null;
            string? sha1hex = null;

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
                using var sha1 = intent.Sha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
                var crc32 = intent.Crc32 ? new System.IO.Hashing.Crc32() : null;
                var crc32c = intent.Crc32C ? new Crc32C() : default;
                var buf = ArrayPool<byte>.Shared.Rent(81920);
                total = 0;

                try
                {
                    int n;
                    using (RequestTrace.Time(Stage.Body))
                    {
                        while ((n = await source.ReadAsync(buf.AsMemory(0, 81920), ct)) > 0)
                        {
                            var span = buf.AsSpan(0, n);
                            sha256.AppendData(span);
                            md5Hash.AppendData(span);
                            sha1?.AppendData(span);
                            crc32?.Append(span);
                            if (intent.Crc32C) crc32c.Append(span);
                            await temp.WriteAsync(buf.AsMemory(0, n), ct);
                            total += n;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                Span<byte> sha256Bytes = stackalloc byte[32];
                sha256.GetHashAndReset(sha256Bytes);
                sha = Convert.ToHexStringLower(sha256Bytes);

                Span<byte> md5Bytes = stackalloc byte[16];
                md5Hash.GetHashAndReset(md5Bytes);
                md5 = Convert.ToHexStringLower(md5Bytes);

                if (sha1 is not null)
                {
                    Span<byte> sha1Bytes = stackalloc byte[20];
                    sha1.GetHashAndReset(sha1Bytes);
                    sha1hex = Convert.ToHexStringLower(sha1Bytes);
                }

                if (crc32 is not null) crc32hex = ChecksumAlgorithms.CrcUInt32ToHex(crc32.GetCurrentHashAsUInt32());
                if (intent.Crc32C) crc32chex = ChecksumAlgorithms.CrcUInt32ToHex(crc32c.GetCurrentHashAndReset());

                using (RequestTrace.Time(Stage.BlobSync))
                {
                    if (fileSync.SyncData(temp) is Result.Failure df) return df.Error;
                }
            }

            if (declaredSize is { } expected && total != expected)
                return new IncompleteBodyError(expected, total);

            var finalPath = PathFor(sha);
            var finalDir = Path.GetDirectoryName(finalPath)!;

            using var publish = RequestTrace.Time(Stage.BlobSync);
            var isNewDir = false;
            if (!ensuredDirs.ContainsKey(finalDir))
            {
                if (fileSync.CreateDirectoryDurable(finalDir) is Result.Failure cf) return cf.Error;
                ensuredDirs.TryAdd(finalDir, true);
                isNewDir = true;
            }

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

            if (isNewDir && fileSync.SyncDirectory(finalDir) is Result.Failure ef)
                return ef.Error;

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

    public Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default)
    {
        var path = PathFor(sha);
        try
        {
            Stream stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            return Task.FromResult<Result<Stream>>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Result<Stream>>(new NotFoundError($"blob {sha}"));
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Result<Stream>>(new NotFoundError($"blob {sha}"));
        }
    }

    public Task<bool> BlobExists(string sha, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(PathFor(sha)));

    public Task<Result<bool>> DeleteBlob(string sha, CancellationToken ct = default)
    {
        var path = PathFor(sha);
        if (!File.Exists(path)) return Task.FromResult<Result<bool>>(false);
        File.Delete(path);
        return Task.FromResult<Result<bool>>(true);
    }

    public DateTime? GetLastWriteUtc(string sha)
    {
        var path = PathFor(sha);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public IEnumerable<string> EnumerateShards()
    {
        var rootFull = Path.GetFullPath(volume.BlobsRoot);
        if (!Directory.Exists(rootFull)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(rootFull))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 2 && name.All(IsHexLower)) yield return name;
        }
    }

    public IEnumerable<string> Enumerate(string shard)
    {
        var dir = Path.Combine(Path.GetFullPath(volume.BlobsRoot), shard);
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (IsLikelySha(name)) yield return name;
        }
    }

    public int ReapAbandonedTempFiles(DateTime cutoffUtc)
    {
        if (!Directory.Exists(volume.TmpDir)) return 0;
        var reaped = 0;
        foreach (var file in Directory.EnumerateFiles(volume.TmpDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                {
                    File.Delete(file);
                    reaped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return reaped;
    }

    private string PathFor(string sha) =>
        Path.Combine(volume.BlobsRoot, sha[..2], sha[2..4], sha);

    private static bool IsLikelySha(string name) =>
        name.Length == 64 && name.All(IsHexLower);

    private static bool IsHexLower(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    private static bool IsOutOfSpace(IOException ex) =>
        ex.HResult is unchecked((int)0x80070027) or unchecked((int)0x80070070)
            || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disk is full", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
