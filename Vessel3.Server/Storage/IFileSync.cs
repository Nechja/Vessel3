using System.Runtime.InteropServices;

namespace Vessel3.Server.Storage;

internal interface IFileSync
{
    Result SyncData(FileStream file);
    Result SyncDirectory(string directory);
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
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
