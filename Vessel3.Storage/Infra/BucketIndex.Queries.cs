using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed partial class BucketIndex
{
    public long MaxSeq()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM versions";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long AppliedSeq()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT COALESCE((SELECT value FROM meta WHERE key = 'applied_seq'), 0)";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public string? GetOwner()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'owner'";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
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
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public Result<PutEntry> GetVersion(string key, string versionId)
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
            : CountVersions(key) > 0 ? new NoSuchVersionError(key, versionId) : new NoSuchKeyError(key);
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

    public string? GetCurrentPutVersionId(string key)
    {
        if (HotCache.TryGet(key, out var cached))
            return cached?.VersionId;

        var expectedVersion = HotCache.GetVersion(key);

        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = """
            SELECT kind, version_id FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        var vid = r.Read() && (VersionKind)r.GetInt32(0) is VersionKind.Put ? r.GetString(1) : null;
        if (vid is null) HotCache.TryFill(key, null, expectedVersion);
        return vid;
    }

    public Result<PutEntry> GetCurrentPut(string key)
    {
        if (HotCache.TryGet(key, out var cached))
            return cached is not null ? cached : new NoSuchKeyError(key);

        var expectedVersion = HotCache.GetVersion(key);

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
        if (!r.Read() || (VersionKind)r.GetInt32(0) is not VersionKind.Put)
        {
            HotCache.TryFill(key, null, expectedVersion);
            return new NoSuchKeyError(key);
        }

        var entry = new PutEntry(
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

        HotCache.TryFill(key, entry, expectedVersion);
        return entry;
    }

    public bool IsEmpty()
    {
        using var rh = ReadCmd();
        var cmd = rh.Cmd;
        cmd.CommandText = "SELECT 1 FROM versions LIMIT 1";
        using var r = cmd.ExecuteReader();
        return !r.Read();
    }

    public async IAsyncEnumerable<string> ReferencedBlobs([EnumeratorCancellation] CancellationToken ct = default)
    {
        const int pageSize = 2000;
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<string> page = [];
            var rows = 0;
            using (var rh = ReadCmd())
            {
                var cmd = rh.Cmd;
                cmd.CommandText = "SELECT seq, blob_sha, parts_json FROM versions WHERE seq > @after ORDER BY seq LIMIT @limit";
                cmd.Parameters.AddWithValue("@after", after);
                cmd.Parameters.AddWithValue("@limit", pageSize);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    rows++;
                    after = r.GetInt64(0);
                    if (r.GetString(1) is { Length: > 0 } blobSha) page.Add(blobSha);
                    if (DeserializeParts(r.GetString(2)) is { } parts)
                        foreach (var p in parts)
                            if (p.BlobSha.Length > 0) page.Add(p.BlobSha);
                }
            }
            foreach (var sha in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return sha;
            }
            if (rows < pageSize) yield break;
        }
    }

    public ObjectLockStatus GetObjectLockStatus(string key, string versionId)
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
        if (!r.Read()) return new ObjectLockStatus(null, false);
        Retention? ret = null;
        if (!r.IsDBNull(0) && !r.IsDBNull(1)
            && Enum.TryParse<RetentionMode>(r.GetString(0), out var mode))
        {
            ret = new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)));
        }
        var hold = !r.IsDBNull(2) && r.GetInt64(2) is 1;
        return new ObjectLockStatus(ret, hold);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static Retention? ReadRetention(SqliteDataReader r, int modeCol, int untilCol) =>
        r.IsDBNull(modeCol) || r.IsDBNull(untilCol)
            || !Enum.TryParse<RetentionMode>(r.GetString(modeCol), out var mode)
                ? null
                : new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(untilCol)));

    private static IReadOnlyDictionary<string, string>? ReadSystemHeaders(SqliteDataReader r, int col)
    {
        if (r.IsDBNull(col)) return null;
        var dict = DeserializeMetadata(r.GetString(col));
        return dict.Count is 0 ? null : dict;
    }

    private static bool ReadLegalHold(SqliteDataReader r, int col) =>
        !r.IsDBNull(col) && r.GetInt64(col) is 1;
}
