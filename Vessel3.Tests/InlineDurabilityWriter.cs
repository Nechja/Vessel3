using Microsoft.Win32.SafeHandles;
using Vessel3.Server.Storage;

namespace Vessel3.Tests;

// Runs the real durability syscalls synchronously on the calling thread — no background
// threads needed in tests, while still exercising the actual fdatasync/fsync path.
internal sealed class InlineDurabilityWriter : IDurabilityWriter
{
    public Task SyncData(SafeFileHandle handle)
    {
        if (PosixFsync.IsLinux) PosixFsync.DataSync(handle);
        return Task.CompletedTask;
    }

    public Task SyncDirectory(string dirPath)
    {
        if (PosixFsync.IsLinux) PosixFsync.SyncDirectory(dirPath);
        return Task.CompletedTask;
    }
}
