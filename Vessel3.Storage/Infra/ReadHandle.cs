using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal readonly struct ReadHandle(SqliteReaderPool pool, SqliteCommand cmd, SqliteConnection conn) : IDisposable
{
    public SqliteCommand Cmd { get; } = cmd;
    private readonly long start = Stopwatch.GetTimestamp();

    public void Dispose()
    {
        RequestTrace.Since(Stage.Query, start);
        Cmd.Dispose();
        pool.Return(conn);
    }
}
