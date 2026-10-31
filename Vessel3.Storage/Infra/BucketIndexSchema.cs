using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal static class BucketIndexSchema
{
    public const string TableVersions = "versions";
    public const string MetaKeyAppliedSeq = "applied_seq";
    public const string MetaKeyOwner = "owner";
    private const string ColTagsJson = "tags_json";
    private const string ColCrc32 = "crc32";
    private const string ColCrc32C = "crc32c";
    private const string ColSha1 = "sha1";
    private const string ColRetentionMode = "retention_mode";
    private const string ColRetainUntil = "retain_until";
    private const string ColLegalHold = "legal_hold";
    private const string ColSystemHeaders = "system_headers";
    private const string ColOriginNode = "origin_node";
    private const string ColOriginSeq = "origin_seq";
    private const string TypeText = "TEXT";
    private const string TypeInteger = "INTEGER";
    private const string TypeTextDefaultEmpty = "TEXT NOT NULL DEFAULT ''";
    private const string TypeIntDefaultZero = "INTEGER NOT NULL DEFAULT 0";

    public static void Initialize(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = 268435456;
            """;
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
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
                system_headers TEXT NOT NULL DEFAULT '{}',
                origin_node   TEXT NOT NULL DEFAULT '',
                origin_seq    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_key_seq ON versions(key, seq DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_key_versionid ON versions(key, version_id);
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS peer_cursors (
                node_id         TEXT PRIMARY KEY,
                last_origin_seq INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        if (!HasColumn(conn, TableVersions, ColTagsJson))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN tags_json TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }

        ReadOnlySpan<string> cols = [ColCrc32, ColCrc32C, ColSha1];
        foreach (var col in cols)
        {
            if (HasColumn(conn, TableVersions, col)) continue;
            using var add = conn.CreateCommand();
            add.CommandText = $"ALTER TABLE versions ADD COLUMN {col} {TypeTextDefaultEmpty}";
            add.ExecuteNonQuery();
        }

        AddColumnIfMissing(conn, ColRetentionMode, TypeText);
        AddColumnIfMissing(conn, ColRetainUntil, TypeInteger);
        AddColumnIfMissing(conn, ColLegalHold, TypeInteger);

        if (!HasColumn(conn, TableVersions, ColSystemHeaders))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN system_headers TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }

        AddColumnIfMissing(conn, ColOriginNode, TypeTextDefaultEmpty);
        AddColumnIfMissing(conn, ColOriginSeq, TypeIntDefaultZero);
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1).Equals(column, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string name, string type)
    {
        if (HasColumn(conn, TableVersions, name)) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE versions ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }
}
