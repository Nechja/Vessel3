using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vessel3.Server.Storage;

internal static class PosixFsync
{
    public static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static void DataSync(SafeFileHandle handle)
    {
        if (!IsLinux) return;
        var fd = handle.DangerousGetHandle().ToInt32();
        if (fdatasync(fd) != 0)
            throw new System.IO.IOException($"fdatasync failed: errno {Marshal.GetLastPInvokeError()}");
    }

    public static void SyncDirectory(string dirPath)
    {
        if (!IsLinux) return;
        var fd = open(dirPath, O_RDONLY | O_DIRECTORY);
        if (fd < 0) throw new System.IO.IOException($"open dir {dirPath} failed: errno {Marshal.GetLastPInvokeError()}");
        try
        {
            if (fsync(fd) != 0)
                throw new System.IO.IOException($"fsync dir {dirPath} failed: errno {Marshal.GetLastPInvokeError()}");
        }
        finally { _ = close(fd); }
    }

    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;

    [DllImport("libc", SetLastError = true)] private static extern int fdatasync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
