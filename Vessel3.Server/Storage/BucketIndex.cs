using System.Globalization;
using Microsoft.Data.Sqlite;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal sealed record VersionEntry(
    string VersionId,
    string BlobSha,
    EventKind Kind,
    long Size,
    string ContentType,
    DateTimeOffset At);

internal sealed record VersionListEntry(
    string Key,
    string VersionId,
    string BlobSha,
    EventKind Kind,
    long Size,
    string ContentType,
    DateTimeOffset At);

// SQLite-backed projection of the VersionLog.
// All SQL lives in this file; everything else sees plain records.
internal sealed class BucketIndex(string dbPath) : IDisposable
{
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

    public void Apply(VersionEvent ev)
    {
        if (ev.Kind == EventKind.HardDelete)
        {
            HardDelete(ev.Key, ev.VersionId);
            return;
        }

        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, kind, size, content_type, at_ms)
            VALUES ($s, $k, $v, $b, $kd, $sz, $ct, $at)
            """;
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$k", ev.Key);
        cmd.Parameters.AddWithValue("$v", ev.VersionId);
        cmd.Parameters.AddWithValue("$b", ev.BlobSha);
        cmd.Parameters.AddWithValue("$kd", (int)ev.Kind);
        cmd.Parameters.AddWithValue("$sz", ev.Size);
        cmd.Parameters.AddWithValue("$ct", ev.ContentType);
        cmd.Parameters.AddWithValue("$at", ev.At.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public bool IsEmpty()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM versions LIMIT 1";
        using var r = cmd.ExecuteReader();
        return !r.Read();
    }

    public Result<VersionEntry?> GetCurrent(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, blob_sha, kind, size, content_type, at_ms
              FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : (VersionEntry?)null;
    }

    public Result<VersionEntry?> GetByVersion(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, blob_sha, kind, size, content_type, at_ms
              FROM versions
             WHERE key = $k AND version_id = $v
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : (VersionEntry?)null;
    }

    // Returns the latest non-DeleteMarker version per key, optionally prefix-filtered.
    public IEnumerable<VersionListEntry> ListCurrent(string? prefix, string? startAfter)
    {
        using var cmd = conn!.CreateCommand();
        var sql = """
            SELECT v1.key, v1.version_id, v1.blob_sha, v1.kind, v1.size, v1.content_type, v1.at_ms
              FROM versions v1
             WHERE v1.seq = (SELECT MAX(seq) FROM versions v2 WHERE v2.key = v1.key)
               AND v1.kind <> 1
            """;
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
        while (r.Read()) yield return ReadListEntry(r);
    }

    // Returns all versions for keys matching prefix, ordered by key then seq desc.
    public IEnumerable<VersionListEntry> ListAllVersions(string? prefix)
    {
        using var cmd = conn!.CreateCommand();
        var sql = """
            SELECT key, version_id, blob_sha, kind, size, content_type, at_ms
              FROM versions
            """;
        if (prefix is not null)
        {
            sql += " WHERE key LIKE $p ESCAPE '\\' ";
            cmd.Parameters.AddWithValue("$p", EscapeLike(prefix) + "%");
        }
        sql += " ORDER BY key, seq DESC";
        cmd.CommandText = sql;

        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return ReadListEntry(r);
    }

    public void HardDelete(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "DELETE FROM versions WHERE key = $k AND version_id = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    // For GC: every blob currently referenced by any non-deleted version.
    public IEnumerable<string> ReferencedBlobs()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT blob_sha FROM versions WHERE blob_sha <> ''";
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return r.GetString(0);
    }

    private VersionEntry ReadEntry(SqliteDataReader r) =>
        new(
            VersionId: r.GetString(0),
            BlobSha: r.GetString(1),
            Kind: (EventKind)r.GetInt32(2),
            Size: r.GetInt64(3),
            ContentType: r.GetString(4),
            At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)));

    private VersionListEntry ReadListEntry(SqliteDataReader r) =>
        new(
            Key: r.GetString(0),
            VersionId: r.GetString(1),
            BlobSha: r.GetString(2),
            Kind: (EventKind)r.GetInt32(3),
            Size: r.GetInt64(4),
            ContentType: r.GetString(5),
            At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)));

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
