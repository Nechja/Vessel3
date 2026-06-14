using System.Text;

namespace Vessel3.Server.Storage;

internal interface IDurableWrite
{
    Result AtomicReplace(string path, string contents);
}

internal sealed class DurableWrite(IFileSync fileSync) : IDurableWrite
{
    public Result AtomicReplace(string path, string contents)
    {
        var tmp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(contents);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            if (fileSync.SyncData(fs) is Result.Failure df) return df.Error;
        }
        File.Move(tmp, path, overwrite: true);
        return fileSync.SyncDirectory(Path.GetDirectoryName(path)!);
    }
}
