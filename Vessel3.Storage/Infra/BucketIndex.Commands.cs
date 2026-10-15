using System.Collections.Frozen;

namespace Vessel3.Storage;

internal sealed partial class BucketIndex
{
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

    public void SetOwner(string ownerId)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES('owner', @ownerId)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        cmd.ExecuteNonQuery();
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
        cmd.Parameters.AddWithValue("$tj", SerializeMetadata(ev.Tags ?? FrozenDictionary<string, string>.Empty));
        cmd.Parameters.AddWithValue("$c32", (object?)ev.Crc32 ?? "");
        cmd.Parameters.AddWithValue("$c32c", (object?)ev.Crc32C ?? "");
        cmd.Parameters.AddWithValue("$s1", (object?)ev.Sha1 ?? "");
        cmd.Parameters.AddWithValue("$rm",
            (object?)ev.RetentionMode?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ru",
            (object?)ev.RetainUntilUnixSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lh", ev.LegalHoldOn ? 1 : 0);
        cmd.Parameters.AddWithValue("$sh", SerializeMetadata(ev.SystemHeaders ?? FrozenDictionary<string, string>.Empty));
        cmd.ExecuteNonQuery();

        var entry = new PutEntry(
            ev.VersionId,
            DateTimeOffset.FromUnixTimeMilliseconds(ev.At.ToUnixTimeMilliseconds()),
            ev.BlobSha,
            ev.Md5,
            ev.Size,
            ev.ContentType,
            ev.Metadata,
            ev.Parts,
            ev.Tags ?? FrozenDictionary<string, string>.Empty,
            ev.Crc32,
            ev.Crc32C,
            ev.Sha1,
            ev.RetentionMode is { } m && ev.RetainUntilUnixSeconds is { } u
                ? new Retention(m, DateTimeOffset.FromUnixTimeSeconds(u))
                : null,
            ev.LegalHoldOn,
            ev.SystemHeaders);

        ApplyCacheAction(() => HotCache.Set(ev.Key, entry));
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
        ApplyCacheAction(() => HotCache.UpdateTags(key, versionId, tags));
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
        ApplyCacheAction(() => HotCache.Set(ev.Key, null));
    }

    public void Remove(string key, string versionId)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = "DELETE FROM versions WHERE key = $k AND version_id = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
        ApplyCacheAction(() => HotCache.Evict(key));
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
        var retention = new Retention(mode, DateTimeOffset.FromUnixTimeSeconds(retainUntilUnixSeconds));
        ApplyCacheAction(() => HotCache.UpdateRetention(key, versionId, retention));
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
        ApplyCacheAction(() => HotCache.UpdateLegalHold(key, versionId, on));
    }
}
