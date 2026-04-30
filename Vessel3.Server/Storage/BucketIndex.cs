using System.Globalization;
using Microsoft.Data.Sqlite;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal sealed class BucketIndex(string dbPath) : IDisposable
{
    private const int KindPut = 0;
    private const int KindDeleteMarker = 1;

    private SqliteConnection? conn;

    public void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
        conn.Open();
        EnsureSchema();
    }

    public void Dispose()
    {
        conn?.Dispose();
        conn = null;
    }

    public long MaxSeq()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM versions";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Insert(PutEvent ev)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms)
            VALUES ($s, $k, $v, $b, $m, $kd, $sz, $ct, $at)
            """;
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$k", ev.Key);
        cmd.Parameters.AddWithValue("$v", ev.VersionId);
        cmd.Parameters.AddWithValue("$b", ev.BlobSha);
        cmd.Parameters.AddWithValue("$m", ev.Md5);
        cmd.Parameters.AddWithValue("$kd", KindPut);
        cmd.Parameters.AddWithValue("$sz", ev.Size);
        cmd.Parameters.AddWithValue("$ct", ev.ContentType);
        cmd.Parameters.AddWithValue("$at", ev.At.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Insert(DeleteMarkerEvent ev)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms)
            VALUES ($s, $k, $v, '', '', $kd, 0, '', $at)
            """;
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$k", ev.Key);
        cmd.Parameters.AddWithValue("$v", ev.VersionId);
        cmd.Parameters.AddWithValue("$kd", KindDeleteMarker);
        cmd.Parameters.AddWithValue("$at", ev.At.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Remove(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "DELETE FROM versions WHERE key = $k AND version_id = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    public Result<PutEntry?> GetCurrentPut(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, blob_sha, md5, size, content_type, at_ms
              FROM versions
             WHERE key = $k AND kind = $kp
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$kp", KindPut);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new PutEntry(
                VersionId: r.GetString(0),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                BlobSha: r.GetString(1),
                Md5: r.GetString(2),
                Size: r.GetInt64(3),
                ContentType: r.GetString(4))
            : (PutEntry?)null;
    }

    public IEnumerable<VersionListEntry> ListCurrent(string? prefix, string? startAfter)
    {
        using var cmd = conn!.CreateCommand();
        var sql = """
            SELECT v1.key, v1.version_id, v1.blob_sha, v1.md5, v1.size, v1.content_type, v1.at_ms
              FROM versions v1
             WHERE v1.seq = (SELECT MAX(seq) FROM versions v2 WHERE v2.key = v1.key)
               AND v1.kind = $kp
            """;
        cmd.Parameters.AddWithValue("$kp", KindPut);
        if (prefix is not null)
        {
            sql += " AND v1.key LIKE $p ESCAPE '\\' ";
            cmd.Parameters.AddWithValue("$p", EscapeLike(prefix) + "%");
        }
        if (startAfter is not null)
        {
            sql += " AND v1.key > $sa ";
            cmd.Parameters.AddWithValue("$sa", startAfter);
        }
        sql += " ORDER BY v1.key";
        cmd.CommandText = sql;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new VersionListEntry(
                Key: r.GetString(0),
                VersionId: r.GetString(1),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                BlobSha: r.GetString(2),
                Md5: r.GetString(3),
                Size: r.GetInt64(4),
                ContentType: r.GetString(5));
        }
    }

    public bool IsEmpty()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM versions LIMIT 1";
        using var r = cmd.ExecuteReader();
        return !r.Read();
    }

    public IEnumerable<string> ReferencedBlobs()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT blob_sha FROM versions WHERE blob_sha <> ''";
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return r.GetString(0);
    }

    private string EscapeLike(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("%", "\\%", StringComparison.Ordinal)
         .Replace("_", "\\_", StringComparison.Ordinal);

    private void EnsureSchema()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS versions (
                seq           INTEGER PRIMARY KEY,
                key           TEXT NOT NULL,
                version_id    TEXT NOT NULL,
                blob_sha      TEXT NOT NULL,
                md5           TEXT NOT NULL DEFAULT '',
                kind          INTEGER NOT NULL,
                size          INTEGER NOT NULL,
                content_type  TEXT NOT NULL,
                at_ms         INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_key_seq ON versions(key, seq DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_key_versionid ON versions(key, version_id);
            """;
        cmd.ExecuteNonQuery();
    }
}
