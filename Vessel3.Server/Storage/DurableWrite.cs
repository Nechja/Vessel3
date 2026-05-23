using System.Text;

namespace Vessel3.Server.Storage;

internal static class DurableWrite
{
    public static void AtomicReplace(string path, string contents)
    {
        var tmp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(contents);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
