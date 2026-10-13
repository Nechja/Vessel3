namespace Vessel3.Storage;

internal sealed partial class BucketIndex
{
    internal static string ListAllVersionsSql(string? prefix, string? hi, string? keyMarker)
    {
        var sql = """
            SELECT key, version_id, kind, md5, size, at_ms, parts_json
              FROM versions
            """;
        List<string> clauses = [];
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

        List<AllVersionsEntry> results = new(limit);
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
            SELECT key, md5, size, at_ms,
                   CASE WHEN parts_json = '' THEN 0 ELSE json_array_length(parts_json) END,
                   MAX(seq)
              FROM versions
            """;
        List<string> clauses = [];
        if (prefix is not null) clauses.Add("key >= $lo");
        if (hi is not null) clauses.Add("key < $hi");
        if (from is { } f) clauses.Add(f.Inclusive ? "key >= $from" : "key > $from");
        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        return sql + " GROUP BY key HAVING kind = $kp ORDER BY key LIMIT $lim";
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

        List<VersionListEntry> results = new(limit);
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
}
