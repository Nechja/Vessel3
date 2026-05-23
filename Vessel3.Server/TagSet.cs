namespace Vessel3.Server;

/// <summary>
/// S3 object-tag validation and the URL-encoded header form parser.
/// Match S3's rules: max 10 tags, key length 1..128, value length 0..256,
/// no empty keys, no duplicate keys. Beyond that be lax — S3 itself accepts
/// arbitrary Unicode in keys/values, so we don't filter characters.
/// </summary>
internal static class TagSet
{
    public const int MaxTags = 10;
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 256;

    public static Result<IReadOnlyDictionary<string, string>> Validate(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            if (string.IsNullOrEmpty(k))
                return new InvalidTagError("tag key cannot be empty");
            if (k.Length > MaxKeyLength)
                return new InvalidTagError($"tag key length {k.Length} exceeds {MaxKeyLength}");
            if (v is null)
                return new InvalidTagError($"tag value for '{k}' cannot be null");
            if (v.Length > MaxValueLength)
                return new InvalidTagError($"tag value length {v.Length} exceeds {MaxValueLength}");
            if (!dict.TryAdd(k, v))
                return new InvalidTagError($"duplicate tag key '{k}'");
            if (dict.Count > MaxTags)
                return new InvalidTagError($"tag set exceeds {MaxTags} entries");
        }
        return new Result<IReadOnlyDictionary<string, string>>.Success(dict);
    }

    /// <summary>
    /// Parse the URL-encoded form used by the `x-amz-tagging` request header.
    /// Format: `k1=v1&k2=v2`. Both sides URL-decoded. Empty/missing header yields empty tags.
    /// </summary>
    public static Result<IReadOnlyDictionary<string, string>> ParseHeader(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new Result<IReadOnlyDictionary<string, string>>.Success(new Dictionary<string, string>());

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var segment in raw.Split('&'))
        {
            if (segment.Length is 0) continue;
            var eq = segment.IndexOf('=', StringComparison.Ordinal);
            string k, v;
            if (eq < 0)
            {
                k = Uri.UnescapeDataString(segment);
                v = string.Empty;
            }
            else
            {
                k = Uri.UnescapeDataString(segment[..eq]);
                v = Uri.UnescapeDataString(segment[(eq + 1)..]);
            }
            pairs.Add(new KeyValuePair<string, string>(k, v));
        }
        return Validate(pairs);
    }
}
