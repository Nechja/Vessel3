using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class SqliteReaderPool(string connectionString, int maxCapacity = 16) : IDisposable
{
    private readonly Lock gate = new();
    private readonly Stack<SqliteConnection> connections = [];
    private bool disposed;

    public SqliteConnection Rent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (connections.TryPop(out var pooled))
                return pooled;
        }

        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA mmap_size = 268435456;
                PRAGMA query_only = ON;
                """;
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    public void Return(SqliteConnection conn)
    {
        lock (gate)
        {
            if (!disposed && connections.Count < maxCapacity && conn.State == System.Data.ConnectionState.Open)
            {
                connections.Push(conn);
                return;
            }
        }

        conn.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            while (connections.TryPop(out var conn))
                conn.Dispose();
        }
    }
}
