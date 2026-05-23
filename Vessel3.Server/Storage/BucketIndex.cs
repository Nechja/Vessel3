using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal sealed class BucketIndex(string dbPath) : IDisposable
{
    internal const int KindPut = 0;
    internal const int KindDeleteMarker = 1;

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
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
               crc32, crc32c, sha1, retention_mode, retain_until, legal_hold)
            VALUES ($s, $k, $v, $b, $m, $kd, $sz, $ct, $at, $mj, $pj, $tj, $c32, $c32c, $s1, $rm, $ru, $lh)
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
        cmd.Parameters.AddWithValue("$tj", SerializeMetadata(ev.Tags ?? new Dictionary<string, string>()));
        cmd.Parameters.AddWithValue("$c32", (object?)ev.Crc32 ?? "");
        cmd.Parameters.AddWithValue("$c32c", (object?)ev.Crc32C ?? "");
        cmd.Parameters.AddWithValue("$s1", (object?)ev.Sha1 ?? "");
        cmd.Parameters.AddWithValue("$rm",
            (object?)ev.RetentionMode?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ru",
            (object?)ev.RetainUntilUnixSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lh", ev.LegalHoldOn ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTags(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "UPDATE versions SET tags_json = $tj WHERE key = $k AND version_id = $v AND kind = $kp";
        cmd.Parameters.AddWithValue("$tj", SerializeMetadata(tags));
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$kp", KindPut);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the kind of a specific version, or null if that version does not exist.
    /// Used by ?tagging routes to distinguish 405 (tagging a delete-marker version) from 404.
    /// </summary>
    public int? GetVersionKind(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT kind FROM versions WHERE key = $k AND version_id = $v LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetInt32(0) : (int?)null;
    }

    /// <summary>
    /// Returns the kind of the current head for a key, or null if no head exists.
    /// Used by ?tagging routes to distinguish 404 (no head) from 405 (delete-marker head).
    /// </summary>
    public int? GetCurrentKind(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT kind FROM versions WHERE key = $k ORDER BY seq DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetInt32(0) : (int?)null;
    }

    public void Insert(DeleteMarkerEvent ev)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
               crc32, crc32c, sha1)
            VALUES ($s, $k, $v, '', '', $kd, 0, '', $at, '{}', '', '{}', '', '', '')
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
            SELECT version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, retention_mode, retain_until, legal_hold
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
                Parts: DeserializeParts(r.GetString(7)),
                Tags: DeserializeMetadata(r.GetString(8)),
                Crc32: NullIfEmpty(r.GetString(9)),
                Crc32C: NullIfEmpty(r.GetString(10)),
                Sha1: NullIfEmpty(r.GetString(11)),
                Retention: ReadRetention(r, 12, 13),
                LegalHoldOn: ReadLegalHold(r, 14))
            : (PutEntry?)null;
    }

    /// <summary>
    /// Returns the version_id of the most-recent row for <paramref name="key"/>
    /// (put or delete-marker), or null if the key has no rows. Used by the Suspended
    /// versioning state machine to detect an existing "null" version that must be
    /// hard-deleted before inserting a new one (the (key, version_id) UNIQUE index
    /// would otherwise reject the insert).
    /// </summary>
    public string? LatestVersionId(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT version_id FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    }

    public Result<PutEntry?> GetCurrentPut(string key)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT kind, version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, retention_mode, retain_until, legal_hold
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
                Parts: DeserializeParts(r.GetString(8)),
                Tags: DeserializeMetadata(r.GetString(9)),
                Crc32: NullIfEmpty(r.GetString(10)),
                Crc32C: NullIfEmpty(r.GetString(11)),
                Sha1: NullIfEmpty(r.GetString(12)),
                Retention: ReadRetention(r, 13, 14),
                LegalHoldOn: ReadLegalHold(r, 15));
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static Retention? ReadRetention(Microsoft.Data.Sqlite.SqliteDataReader r, int modeCol, int untilCol) =>
        r.IsDBNull(modeCol) || r.IsDBNull(untilCol)
            || !Enum.TryParse<RetentionMode>(r.GetString(modeCol), out var mode)
                ? null
                : new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(untilCol)));

    private static bool ReadLegalHold(Microsoft.Data.Sqlite.SqliteDataReader r, int col) =>
        !r.IsDBNull(col) && r.GetInt64(col) is 1;

    public (List<AllVersionsEntry> Entries, bool IsTruncated) ListAllVersions(string? prefix, string? keyMarker, int limit)
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
        sql += " ORDER BY key ASC, seq DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit + 1);
        cmd.CommandText = sql;

        var results = new List<AllVersionsEntry>(limit);
        using var r = cmd.ExecuteReader();
        string? lastKey = null;
        var truncated = false;
        while (r.Read())
        {
            if (results.Count >= limit) { truncated = true; break; }
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
        return (results, truncated);
    }

    public List<VersionListEntry> ListCurrent(string? prefix, string? startAfter)
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

        var results = new List<VersionListEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new VersionListEntry(
                Key: r.GetString(0),
                VersionId: r.GetString(1),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                BlobSha: r.GetString(2),
                Md5: r.GetString(3),
                Size: r.GetInt64(4),
                ContentType: r.GetString(5),
                Metadata: DeserializeMetadata(r.GetString(7)),
                Parts: DeserializeParts(r.GetString(8))));
        }
        return results;
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
        using var pragma = conn!.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();

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
                parts_json    TEXT NOT NULL DEFAULT '',
                tags_json     TEXT NOT NULL DEFAULT '{}',
                crc32         TEXT NOT NULL DEFAULT '',
                crc32c        TEXT NOT NULL DEFAULT '',
                sha1          TEXT NOT NULL DEFAULT '',
                retention_mode TEXT,
                retain_until  INTEGER,
                legal_hold    INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_key_seq ON versions(key, seq DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_key_versionid ON versions(key, version_id);
            """;
        cmd.ExecuteNonQuery();

        // Additive columns added after the original schema; tolerate older DBs.
        if (!HasColumn("versions", "tags_json"))
        {
            using var alter = conn!.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN tags_json TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }
        foreach (var col in new[] { "crc32", "crc32c", "sha1" })
        {
            if (HasColumn("versions", col)) continue;
            using var add = conn!.CreateCommand();
            add.CommandText = $"ALTER TABLE versions ADD COLUMN {col} TEXT NOT NULL DEFAULT ''";
            add.ExecuteNonQuery();
        }
        AddColumnIfMissing("retention_mode", "TEXT");
        AddColumnIfMissing("retain_until", "INTEGER");
        AddColumnIfMissing("legal_hold", "INTEGER");
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1).Equals(column, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void AddColumnIfMissing(string name, string type)
    {
        if (HasColumn("versions", name)) return;
        using var alter = conn!.CreateCommand();
        alter.CommandText = $"ALTER TABLE versions ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }

    public void ApplyRetention(string key, string versionId, RetentionMode mode, long retainUntilUnixSeconds)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            UPDATE versions
               SET retention_mode = $rm, retain_until = $ru
             WHERE key = $k AND version_id = $v
            """;
        cmd.Parameters.AddWithValue("$rm", mode.ToString());
        cmd.Parameters.AddWithValue("$ru", retainUntilUnixSeconds);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    public void ApplyLegalHold(string key, string versionId, bool on)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            UPDATE versions
               SET legal_hold = $lh
             WHERE key = $k AND version_id = $v
            """;
        cmd.Parameters.AddWithValue("$lh", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    public (Retention? Retention, bool LegalHoldOn) GetLock(string key, string versionId)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = """
            SELECT retention_mode, retain_until, legal_hold
              FROM versions
             WHERE key = $k AND version_id = $v
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, false);
        Retention? ret = null;
        if (!r.IsDBNull(0) && !r.IsDBNull(1)
            && Enum.TryParse<RetentionMode>(r.GetString(0), out var mode))
        {
            ret = new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)));
        }
        var hold = !r.IsDBNull(2) && r.GetInt64(2) is 1;
        return (ret, hold);
    }
}
