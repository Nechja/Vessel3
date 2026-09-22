using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal enum VersionKind { Put = 0, DeleteMarker = 1 }

internal readonly record struct KeyBound(string Key, bool Inclusive)
{
    public static KeyBound After(string key) => new(key, false);
    public static KeyBound From(string key) => new(key, true);
}

internal sealed class BucketIndex(string dbPath) : IDisposable
{
    private SqliteConnection? writeConn;
    private SqliteConnection? readConn;
    private SqliteTransaction? currentTx;
    private readonly object readGate = new();

    public void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        writeConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
        writeConn.Open();
        using (var pragma = writeConn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema();

        readConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        readConn.Open();
    }

    public void Dispose()
    {
        readConn?.Dispose();
        readConn = null;
        writeConn?.Dispose();
        writeConn = null;
    }

    public TxScope BeginTransaction()
    {
        var tx = writeConn!.BeginTransaction();
        currentTx = tx;
        return new TxScope(this, tx);
    }

    public sealed class TxScope : IDisposable
    {
        private readonly BucketIndex owner;
        private readonly SqliteTransaction tx;
        private bool committed;
        internal TxScope(BucketIndex owner, SqliteTransaction tx) { this.owner = owner; this.tx = tx; }
        public void Commit() { tx.Commit(); committed = true; }
        public void Dispose()
        {
            if (!committed) tx.Rollback();
            tx.Dispose();
            if (ReferenceEquals(owner.currentTx, tx)) owner.currentTx = null;
        }
    }

    private SqliteCommand WriteCmd()
    {
        var c = writeConn!.CreateCommand();
        if (currentTx is not null) c.Transaction = currentTx;
        return c;
    }

    private ReadHandle ReadCmd()
    {
        var connection = readConn ?? throw new ObjectDisposedException(nameof(BucketIndex));
        var start = Stopwatch.GetTimestamp();
        Monitor.Enter(readGate);
        RequestTrace.Since(Stage.ReadLock, start);
        try
        {
            return new ReadHandle(connection.CreateCommand(), readGate);
        }
        catch
        {
            Monitor.Exit(readGate);
            throw;
        }
    }

    internal readonly struct ReadHandle(SqliteCommand cmd, object gate) : IDisposable
    {
        public SqliteCommand Cmd { get; } = cmd;
        private readonly object gate = gate;
        private readonly long start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            RequestTrace.Since(Stage.Query, start);
            Cmd.Dispose();
            Monitor.Exit(gate);
        }
    }

    public long MaxSeq()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM versions";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void MarkApplied(long seq)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES('applied_seq', @seq)
            ON CONFLICT(key) DO UPDATE SET value = max(value, excluded.value)
            """;
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.ExecuteNonQuery();
    }

    public long AppliedSeq()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COALESCE((SELECT value FROM meta WHERE key = 'applied_seq'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void SnapshotTo(string path)
    {
        using var cmd = writeConn!.CreateCommand();
        cmd.CommandText = "VACUUM INTO @path";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }

    public void Insert(PutEvent ev)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
               crc32, crc32c, sha1, retention_mode, retain_until, legal_hold, system_headers)
            VALUES ($s, $k, $v, $b, $m, $kd, $sz, $ct, $at, $mj, $pj, $tj, $c32, $c32c, $s1, $rm, $ru, $lh, $sh)
            """;
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$k", ev.Key);
        cmd.Parameters.AddWithValue("$v", ev.VersionId);
        cmd.Parameters.AddWithValue("$b", ev.BlobSha);
        cmd.Parameters.AddWithValue("$m", ev.Md5);
        cmd.Parameters.AddWithValue("$kd", (int)VersionKind.Put);
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
        cmd.Parameters.AddWithValue("$sh", SerializeMetadata(ev.SystemHeaders ?? new Dictionary<string, string>()));
        cmd.ExecuteNonQuery();
    }

    public void UpdateTags(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = "UPDATE versions SET tags_json = $tj WHERE key = $k AND version_id = $v AND kind = $kp";
        cmd.Parameters.AddWithValue("$tj", SerializeMetadata(tags));
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$kp", (int)VersionKind.Put);
        cmd.ExecuteNonQuery();
    }

    public VersionKind? GetVersionKind(string key, string versionId)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT kind FROM versions WHERE key = $k AND version_id = $v LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (VersionKind)r.GetInt32(0) : (VersionKind?)null;
    }

    public VersionKind? GetCurrentKind(string key)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = """
            SELECT kind FROM versions WHERE key = $k ORDER BY seq DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (VersionKind)r.GetInt32(0) : (VersionKind?)null;
    }

    public long VersionCount()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COUNT(*) FROM versions";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountVersions(string key)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COUNT(*) FROM versions WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Insert(DeleteMarkerEvent ev)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT OR IGNORE INTO versions
              (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
               crc32, crc32c, sha1)
            VALUES ($s, $k, $v, '', '', $kd, 0, '', $at, '{}', '', '{}', '', '', '')
            """;
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$k", ev.Key);
        cmd.Parameters.AddWithValue("$v", ev.VersionId);
        cmd.Parameters.AddWithValue("$kd", (int)VersionKind.DeleteMarker);
        cmd.Parameters.AddWithValue("$at", ev.At.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Remove(string key, string versionId)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = "DELETE FROM versions WHERE key = $k AND version_id = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    public Result<PutEntry?> GetVersion(string key, string versionId)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = """
            SELECT version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, retention_mode, retain_until, legal_hold, system_headers
              FROM versions
             WHERE key = $k AND version_id = $v AND kind = $kp
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$kp", (int)VersionKind.Put);
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
                LegalHoldOn: ReadLegalHold(r, 14),
                SystemHeaders: ReadSystemHeaders(r, 15))
            : (PutEntry?)null;
    }

    public string? LatestVersionId(string key)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
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
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = """
            SELECT kind, version_id, blob_sha, md5, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, retention_mode, retain_until, legal_hold, system_headers
              FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return !r.Read() || (VersionKind)r.GetInt32(0) is not VersionKind.Put
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
                LegalHoldOn: ReadLegalHold(r, 15),
                SystemHeaders: ReadSystemHeaders(r, 16));
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static Retention? ReadRetention(Microsoft.Data.Sqlite.SqliteDataReader r, int modeCol, int untilCol) =>
        r.IsDBNull(modeCol) || r.IsDBNull(untilCol)
            || !Enum.TryParse<RetentionMode>(r.GetString(modeCol), out var mode)
                ? null
                : new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(untilCol)));

    private IReadOnlyDictionary<string, string>? ReadSystemHeaders(Microsoft.Data.Sqlite.SqliteDataReader r, int col)
    {
        if (r.IsDBNull(col)) return null;
        var dict = DeserializeMetadata(r.GetString(col));
        return dict.Count is 0 ? null : dict;
    }

    private static bool ReadLegalHold(Microsoft.Data.Sqlite.SqliteDataReader r, int col) =>
        !r.IsDBNull(col) && r.GetInt64(col) is 1;

    internal static string ListAllVersionsSql(string? prefix, string? hi, string? keyMarker)
    {
        var sql = """
            SELECT key, version_id, kind, md5, size, at_ms, parts_json
              FROM versions
            """;
        var clauses = new List<string>();
        if (prefix is not null) clauses.Add("key >= $lo");
        if (hi is not null) clauses.Add("key < $hi");
        if (keyMarker is not null) clauses.Add("key > $km");
        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        return sql + " ORDER BY key ASC, seq DESC LIMIT $lim";
    }

    public (List<AllVersionsEntry> Entries, bool IsTruncated) ListAllVersions(string? prefix, string? keyMarker, int limit)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        var hi = prefix is null ? null : KeyRange.Successor(prefix);
        cmd.CommandText = ListAllVersionsSql(prefix, hi, keyMarker);
        if (prefix is not null) cmd.Parameters.AddWithValue("$lo", prefix);
        if (hi is not null) cmd.Parameters.AddWithValue("$hi", hi);
        if (keyMarker is not null) cmd.Parameters.AddWithValue("$km", keyMarker);
        cmd.Parameters.AddWithValue("$lim", limit + 1);

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
            var kind = (VersionKind)r.GetInt32(2);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5));
            results.Add(kind is VersionKind.Put
                ? new AllVersionsEntry.Put(key, versionId, at, isLatest,
                    r.GetString(3), r.GetInt64(4), DeserializeParts(r.GetString(6)))
                : new AllVersionsEntry.Marker(key, versionId, at, isLatest));
        }
        return (results, truncated);
    }

    internal static string ListCurrentSql(string? prefix, string? hi, KeyBound? from)
    {
        var sql = """
            SELECT v1.key, v1.md5, v1.size, v1.at_ms,
                   CASE WHEN v1.parts_json = '' THEN 0 ELSE json_array_length(v1.parts_json) END
              FROM versions v1
             WHERE v1.seq = (SELECT MAX(seq) FROM versions v2 WHERE v2.key = v1.key)
               AND v1.kind = $kp
            """;
        if (prefix is not null) sql += " AND v1.key >= $lo ";
        if (hi is not null) sql += " AND v1.key < $hi ";
        if (from is { } f) sql += f.Inclusive ? " AND v1.key >= $from " : " AND v1.key > $from ";
        return sql + " ORDER BY v1.key LIMIT $lim";
    }

    public (List<VersionListEntry> Entries, bool IsTruncated) ListCurrent(string? prefix, KeyBound? from, int limit)
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        var hi = prefix is null ? null : KeyRange.Successor(prefix);
        cmd.CommandText = ListCurrentSql(prefix, hi, from);
        cmd.Parameters.AddWithValue("$kp", (int)VersionKind.Put);
        if (prefix is not null) cmd.Parameters.AddWithValue("$lo", prefix);
        if (hi is not null) cmd.Parameters.AddWithValue("$hi", hi);
        if (from is { } f) cmd.Parameters.AddWithValue("$from", f.Key);
        cmd.Parameters.AddWithValue("$lim", limit + 1);

        var results = new List<VersionListEntry>(limit);
        using var r = cmd.ExecuteReader();
        var truncated = false;
        while (r.Read())
        {
            if (results.Count >= limit) { truncated = true; break; }
            results.Add(new VersionListEntry(
                Key: r.GetString(0),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                Md5: r.GetString(1),
                Size: r.GetInt64(2),
                PartCount: r.GetInt32(4)));
        }
        return (results, truncated);
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
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT 1 FROM versions LIMIT 1";
        using var r = cmd.ExecuteReader();
        return !r.Read();
    }

    public IEnumerable<string> ReferencedBlobs()
    {
        const int pageSize = 10000;
        long after = 0;
        while (true)
        {
            var page = new List<string>();
            var rows = 0;
            using (var rh = ReadCmd())
            {
                var cmd = rh.Cmd;
                cmd.CommandText = "SELECT seq, blob_sha, parts_json FROM versions WHERE seq > @after ORDER BY seq LIMIT @limit";
                cmd.Parameters.AddWithValue("@after", after);
                cmd.Parameters.AddWithValue("@limit", pageSize);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows++;
                    after = r.GetInt64(0);
                    if (r.GetString(1) is { Length: > 0 } blobSha) page.Add(blobSha);
                    if (DeserializeParts(r.GetString(2)) is { } parts)
                        foreach (var p in parts)
                            if (p.BlobSha.Length > 0) page.Add(p.BlobSha);
                }
            }
            foreach (var sha in page) yield return sha;
            if (rows < pageSize) yield break;
        }
    }

    private void EnsureSchema()
    {
        using var pragma = writeConn!.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();

        using var cmd = WriteCmd();
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
                legal_hold    INTEGER,
                system_headers TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_key_seq ON versions(key, seq DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_key_versionid ON versions(key, version_id);
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        if (!HasColumn("versions", "tags_json"))
        {
            using var alter = writeConn!.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN tags_json TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }
        foreach (var col in new[] { "crc32", "crc32c", "sha1" })
        {
            if (HasColumn("versions", col)) continue;
            using var add = writeConn!.CreateCommand();
            add.CommandText = $"ALTER TABLE versions ADD COLUMN {col} TEXT NOT NULL DEFAULT ''";
            add.ExecuteNonQuery();
        }
        AddColumnIfMissing("retention_mode", "TEXT");
        AddColumnIfMissing("retain_until", "INTEGER");
        AddColumnIfMissing("legal_hold", "INTEGER");
        if (!HasColumn("versions", "system_headers"))
        {
            using var alter = writeConn!.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN system_headers TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = WriteCmd();
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
        using var alter = writeConn!.CreateCommand();
        alter.CommandText = $"ALTER TABLE versions ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }

    public void ApplyRetention(string key, string versionId, RetentionMode mode, long retainUntilUnixSeconds)
    {
        using var cmd = WriteCmd();
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
        using var cmd = WriteCmd();
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
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
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
