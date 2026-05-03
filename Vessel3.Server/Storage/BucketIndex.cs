using System.Globalization;
using System.Text.Json;
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
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json)
            VALUES ($s, $k, $v, $b, $m, $kd, $sz, $ct, $at, $mj, $pj)
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
        cmd.Parameters.AddWithValue("$mj", SerializeMetadata(ev.Metadata));
        cmd.Parameters.AddWithValue("$pj", SerializeParts(ev.Parts));
        cmd.ExecuteNonQuery();
    }

    public void Insert(DeleteMarkerEvent ev)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json)
            VALUES ($s, $k, $v, '', '', $kd, 0, '', $at, '{}', '')
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

    public Result<PutEntry?> GetVersion(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json
              FROM versions
             WHERE key = $k AND version_id = $v AND kind = $kp
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$kp", KindPut);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new PutEntry(
                VersionId: r.GetString(0),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                BlobSha: r.GetString(1),
                Md5: r.GetString(2),
                Size: r.GetInt64(3),
                ContentType: r.GetString(4),
                Metadata: DeserializeMetadata(r.GetString(6)),
                Parts: DeserializeParts(r.GetString(7)))
            : (PutEntry?)null;
    }

    public Result<PutEntry?> GetCurrentPut(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT kind, version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json
              FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return !r.Read() || r.GetInt32(0) is not KindPut
            ? (PutEntry?)null
            : new PutEntry(
                VersionId: r.GetString(1),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                BlobSha: r.GetString(2),
                Md5: r.GetString(3),
                Size: r.GetInt64(4),
                ContentType: r.GetString(5),
                Metadata: DeserializeMetadata(r.GetString(7)),
                Parts: DeserializeParts(r.GetString(8)));
    }

    public List<AllVersionsEntry> ListAllVersions(string? prefix, string? keyMarker)
    {
        using var cmd = conn!.CreateCommand();
        var sql = """
            SELECT key, version_id, kind, md5, size, at_ms, parts_json
              FROM versions
            """;
        var clauses = new List<string>();
        if (prefix is not null)
        {
            clauses.Add("key LIKE $p ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$p", EscapeLike(prefix) + "%");
        }
        if (keyMarker is not null)
        {
            clauses.Add("key > $km");
            cmd.Parameters.AddWithValue("$km", keyMarker);
        }
        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        sql += " ORDER BY key ASC, seq DESC";
        cmd.CommandText = sql;

        var results = new List<AllVersionsEntry>();
        using var r = cmd.ExecuteReader();
        string? lastKey = null;
        while (r.Read())
        {
            var key = r.GetString(0);
            var isLatest = key != lastKey;
            lastKey = key;
            var versionId = r.GetString(1);
            var kind = r.GetInt32(2);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5));
            results.Add(kind is KindPut
                ? new AllVersionsEntry.Put(key, versionId, at, isLatest,
                    r.GetString(3), r.GetInt64(4), DeserializeParts(r.GetString(6)))
                : new AllVersionsEntry.Marker(key, versionId, at, isLatest));
        }
        return results;
    }

    public IEnumerable<VersionListEntry> ListCurrent(string? prefix, string? startAfter)
    {
        using var cmd = conn!.CreateCommand();
        var sql = """
            SELECT v1.key, v1.version_id, v1.blob_sha, v1.md5, v1.size, v1.content_type, v1.at_ms, v1.md_json, v1.parts_json
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
                ContentType: r.GetString(5),
                Metadata: DeserializeMetadata(r.GetString(7)),
                Parts: DeserializeParts(r.GetString(8)));
        }
    }

    private string SerializeMetadata(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Count is 0 ? "{}"
            : JsonSerializer.Serialize(
                new Dictionary<string, string>(metadata),
                VersionEventContext.Default.DictionaryStringString);

    private IReadOnlyDictionary<string, string> DeserializeMetadata(string json) =>
        string.IsNullOrEmpty(json) || json is "{}"
            ? []
            : JsonSerializer.Deserialize(json, VersionEventContext.Default.DictionaryStringString)
                ?? [];

    private string SerializeParts(IReadOnlyList<MultipartPart>? parts) =>
        parts is null || parts.Count is 0 ? ""
            : JsonSerializer.Serialize(
                [.. parts],
                VersionEventContext.Default.ListMultipartPart);

    private IReadOnlyList<MultipartPart>? DeserializeParts(string json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, VersionEventContext.Default.ListMultipartPart);

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
                at_ms         INTEGER NOT NULL,
                md_json       TEXT NOT NULL DEFAULT '{}',
                parts_json    TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_key_seq ON versions(key, seq DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_key_versionid ON versions(key, version_id);
            """;
        cmd.ExecuteNonQuery();

        if (!ColumnExists("versions", "parts_json"))
        {
            using var alter = conn!.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN parts_json TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.Ordinal))
                return true;
        return false;
    }
}
