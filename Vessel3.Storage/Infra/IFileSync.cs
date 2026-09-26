using System.Runtime.InteropServices;

namespace Vessel3.Storage;

internal interface IFileSync
{
    Result SyncData(FileStream file);
    Result SyncDirectory(string directory);
}

internal static class FileSyncExtensions
{
    public static Result CreateDirectoryDurable(this IFileSync fileSync, string dir)
    {
        var missingLevels = MissingLevels(dir);
        if (missingLevels.Count == 0) return Result.Ok;

        Directory.CreateDirectory(dir);

        foreach (var level in missingLevels)
            if (ParentOf(level) is { } parent && fileSync.SyncDirectory(parent) is Result.Failure f)
                return f.Error;
        return Result.Ok;
    }

    private static List<string> MissingLevels(string dir)
    {
        var missing = new List<string>();
        for (var level = dir; level is not null && !Directory.Exists(level); level = ParentOf(level))
            missing.Add(level);
        return missing;
    }

    private static string? ParentOf(string dir) => Path.GetDirectoryName(dir);
}

internal sealed class PortableFileSync : IFileSync
{
    public Result SyncData(FileStream file)
    {
        file.Flush(flushToDisk: true);
        return Result.Ok;
    }

    public Result SyncDirectory(string directory) => Result.Ok;
}

internal sealed class PosixFileSync : IFileSync
{
    public Result SyncData(FileStream file)
    {
        file.Flush();
        return fdatasync((int)file.SafeFileHandle.DangerousGetHandle()) == 0 ? Result.Ok : Fail("fdatasync");
    }

    public Result SyncDirectory(string directory)
    {
        var fd = open(directory, O_RDONLY | O_DIRECTORY);
        if (fd < 0) return Fail("open");
        try
        {
            return fsync(fd) == 0 ? Result.Ok : Fail("fsync");
        }
        finally { _ = close(fd); }
    }

    private Result Fail(string op)
    {
        var errno = Marshal.GetLastPInvokeError();
        return errno == ENOSPC
            ? new InsufficientStorageError($"{op}: errno {errno}")
            : new DurabilityError($"{op}: errno {errno}");
    }

    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int ENOSPC = 28;

    [DllImport("libc", SetLastError = true)] private static extern int fdatasync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
