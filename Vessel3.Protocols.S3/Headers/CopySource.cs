namespace Vessel3.Server.S3;

internal sealed record CopySource(string Bucket, string Key)
{
    public static bool TryParse(string? raw, out CopySource? source)
    {
        source = null;
        if (!TryParse(raw, out string b, out string k))
        {
            return false;
        }

        source = new CopySource(b, k);
        return true;
    }

    public static bool TryParse(string? raw, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var trimmed = raw.StartsWith('/')
            ? raw[1..]
            : raw.StartsWith("%2F", StringComparison.OrdinalIgnoreCase)
                ? raw[3..]
                : raw;

        var qm = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (qm >= 0)
        {
            trimmed = trimmed[..qm];
        }

        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        var enc = trimmed.IndexOf("%2F", StringComparison.OrdinalIgnoreCase);
        var (sep, sepLen) = enc >= 0 && (slash < 0 || enc < slash) ? (enc, 3) : (slash, 1);
        if (sep <= 0 || sep + sepLen >= trimmed.Length)
        {
            return false;
        }

        bucket = trimmed[..sep];
        key = Uri.UnescapeDataString(trimmed[(sep + sepLen)..]);
        return true;
    }
}
