using System.Collections.Frozen;

namespace Vessel3.Storage;

internal sealed partial class BucketIndex
{
    public void MarkApplied(long seq)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES(@key, @seq)
            ON CONFLICT(key) DO UPDATE SET value = max(value, excluded.value)
            """;
        cmd.Parameters.AddWithValue("@key", BucketIndexSchema.MetaKeyAppliedSeq);
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.ExecuteNonQuery();
        ApplyCacheAction(() =>
        {
            var current = Volatile.Read(ref cachedAppliedSeq);
            if (seq > current)
                Volatile.Write(ref cachedAppliedSeq, seq);
        });
    }

    public void SetOwner(string ownerId)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES(@key, @ownerId)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@key", BucketIndexSchema.MetaKeyOwner);
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        cmd.ExecuteNonQuery();
        ApplyCacheAction(() => Volatile.Write(ref cachedOwner, ownerId));
    }

    public void SetPeerCursor(string nodeId, long originSeq)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            INSERT INTO peer_cursors(node_id, last_origin_seq) VALUES(@nodeId, @seq)
            ON CONFLICT(node_id) DO UPDATE SET last_origin_seq = max(last_origin_seq, excluded.last_origin_seq)
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@seq", originSeq);
        cmd.ExecuteNonQuery();
    }

    public void PurgeSupersededVersionsExceptLatest(string key)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = """
            DELETE FROM versions
             WHERE key = $k
               AND seq < (SELECT COALESCE(MAX(seq), 0) FROM versions WHERE key = $k)
            """;
        cmd.Parameters.AddWithValue("$k", key);
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
        var evAtMs = ev.At.ToUnixTimeMilliseconds();
        var evOriginNode = ev.OriginNodeId ?? string.Empty;
        var evOriginSeq = ev.OriginSeq > 0 ? ev.OriginSeq : ev.Seq;

        var isOlderCrossNode = TryDetectOlderCrossNodeEvent(ev.Key, evAtMs, evOriginNode, out var topSeq, out var topVid);
        if (isOlderCrossNode && string.Equals(ev.VersionId, topVid, StringComparison.Ordinal))
            return;

        var targetSeq = isOlderCrossNode ? topSeq : ev.Seq;
        if (isOlderCrossNode)
            ShiftRowSeq(topSeq, -topSeq);

        using (var cmd = WriteCmd())
        {
            cmd.CommandText = """
                INSERT INTO versions
                  (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, retention_mode, retain_until, legal_hold, system_headers, origin_node, origin_seq)
                VALUES ($s, $k, $v, $b, $m, $kd, $sz, $ct, $at, $mj, $pj, $tj, $c32, $c32c, $s1, $rm, $ru, $lh, $sh, $on, $os)
                ON CONFLICT(key, version_id) DO UPDATE SET
                  seq = excluded.seq,
                  blob_sha = excluded.blob_sha,
                  md5 = excluded.md5,
                  kind = excluded.kind,
                  size = excluded.size,
                  content_type = excluded.content_type,
                  at_ms = excluded.at_ms,
                  md_json = excluded.md_json,
                  parts_json = excluded.parts_json,
                  tags_json = excluded.tags_json,
                  crc32 = excluded.crc32,
                  crc32c = excluded.crc32c,
                  sha1 = excluded.sha1,
                  retention_mode = excluded.retention_mode,
                  retain_until = excluded.retain_until,
                  legal_hold = excluded.legal_hold,
                  system_headers = excluded.system_headers,
                  origin_node = excluded.origin_node,
                  origin_seq = excluded.origin_seq
                """;
            cmd.Parameters.AddWithValue("$s", targetSeq);
            cmd.Parameters.AddWithValue("$k", ev.Key);
            cmd.Parameters.AddWithValue("$v", ev.VersionId);
            cmd.Parameters.AddWithValue("$b", ev.BlobSha);
            cmd.Parameters.AddWithValue("$m", ev.Md5);
            cmd.Parameters.AddWithValue("$kd", (int)VersionKind.Put);
            cmd.Parameters.AddWithValue("$sz", ev.Size);
            cmd.Parameters.AddWithValue("$ct", ev.ContentType);
            cmd.Parameters.AddWithValue("$at", evAtMs);
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
            cmd.Parameters.AddWithValue("$on", evOriginNode);
            cmd.Parameters.AddWithValue("$os", evOriginSeq);
            cmd.ExecuteNonQuery();
        }

        if (isOlderCrossNode)
        {
            ShiftRowSeq(-topSeq, ev.Seq);
            return;
        }

        var entry = new PutEntry(
            ev.VersionId,
            DateTimeOffset.FromUnixTimeMilliseconds(evAtMs),
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
        var evAtMs = ev.At.ToUnixTimeMilliseconds();
        var evOriginNode = ev.OriginNodeId ?? string.Empty;
        var evOriginSeq = ev.OriginSeq > 0 ? ev.OriginSeq : ev.Seq;

        var isOlderCrossNode = TryDetectOlderCrossNodeEvent(ev.Key, evAtMs, evOriginNode, out var topSeq, out var topVid);
        if (isOlderCrossNode && string.Equals(ev.VersionId, topVid, StringComparison.Ordinal))
            return;

        var targetSeq = isOlderCrossNode ? topSeq : ev.Seq;
        if (isOlderCrossNode)
            ShiftRowSeq(topSeq, -topSeq);

        using (var cmd = WriteCmd())
        {
            cmd.CommandText = """
                INSERT INTO versions
                  (seq, key, version_id, blob_sha, md5, kind, size, content_type, at_ms, md_json, parts_json, tags_json,
                   crc32, crc32c, sha1, origin_node, origin_seq)
                VALUES ($s, $k, $v, '', '', $kd, 0, '', $at, '{}', '', '{}', '', '', '', $on, $os)
                ON CONFLICT(key, version_id) DO UPDATE SET
                  seq = excluded.seq,
                  kind = excluded.kind,
                  at_ms = excluded.at_ms,
                  origin_node = excluded.origin_node,
                  origin_seq = excluded.origin_seq
                """;
            cmd.Parameters.AddWithValue("$s", targetSeq);
            cmd.Parameters.AddWithValue("$k", ev.Key);
            cmd.Parameters.AddWithValue("$v", ev.VersionId);
            cmd.Parameters.AddWithValue("$kd", (int)VersionKind.DeleteMarker);
            cmd.Parameters.AddWithValue("$at", evAtMs);
            cmd.Parameters.AddWithValue("$on", evOriginNode);
            cmd.Parameters.AddWithValue("$os", evOriginSeq);
            cmd.ExecuteNonQuery();
        }

        if (isOlderCrossNode)
        {
            ShiftRowSeq(-topSeq, ev.Seq);
            return;
        }

        ApplyCacheAction(() => HotCache.Set(ev.Key, null));
    }

    private bool TryDetectOlderCrossNodeEvent(
        string key,
        long incomingAtMs,
        string incomingOriginNode,
        out long topSeq,
        out string topVersionId)
    {
        topSeq = 0;
        topVersionId = string.Empty;
        if (string.IsNullOrEmpty(incomingOriginNode))
            return false;

        using var cmd = WriteCmd();
        cmd.CommandText = """
            SELECT seq, version_id, at_ms, origin_node
              FROM versions
             WHERE key = $k
             ORDER BY seq DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return false;

        topSeq = r.GetInt64(0);
        topVersionId = r.GetString(1);
        var topAtMs = r.GetInt64(2);
        var topOriginNode = r.GetString(3);

        if (string.IsNullOrEmpty(topOriginNode) || string.Equals(topOriginNode, incomingOriginNode, StringComparison.Ordinal))
            return false;

        return incomingAtMs < topAtMs
            || (incomingAtMs == topAtMs && string.CompareOrdinal(incomingOriginNode, topOriginNode) < 0);
    }

    private void ShiftRowSeq(long fromSeq, long toSeq)
    {
        using var cmd = WriteCmd();
        cmd.CommandText = "UPDATE versions SET seq = $to WHERE seq = $from";
        cmd.Parameters.AddWithValue("$from", fromSeq);
        cmd.Parameters.AddWithValue("$to", toSeq);
        cmd.ExecuteNonQuery();
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
