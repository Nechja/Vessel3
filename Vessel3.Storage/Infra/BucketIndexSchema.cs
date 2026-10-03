using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal static class BucketIndexSchema
{
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

        if (!HasColumn(conn, "versions", "tags_json"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN tags_json TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }

        ReadOnlySpan<string> cols = ["crc32", "crc32c", "sha1"];
        foreach (var col in cols)
        {
            if (HasColumn(conn, "versions", col)) continue;
            using var add = conn.CreateCommand();
            add.CommandText = $"ALTER TABLE versions ADD COLUMN {col} TEXT NOT NULL DEFAULT ''";
            add.ExecuteNonQuery();
        }

        AddColumnIfMissing(conn, "retention_mode", "TEXT");
        AddColumnIfMissing(conn, "retain_until", "INTEGER");
        AddColumnIfMissing(conn, "legal_hold", "INTEGER");

        if (!HasColumn(conn, "versions", "system_headers"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE versions ADD COLUMN system_headers TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
        }
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
        if (HasColumn(conn, "versions", name)) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE versions ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }
}
