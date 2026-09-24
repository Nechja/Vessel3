using System.Collections.Frozen;

namespace Vessel3.Server;

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
        return dict;
    }

    public static Result<IReadOnlyDictionary<string, string>> ParseHeader(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return FrozenDictionary<string, string>.Empty;

        List<KeyValuePair<string, string>> pairs = [];
        foreach (var segment in raw.Split('&'))
        {
            if (segment.Length is 0) continue;
            var eq = segment.IndexOf('=', StringComparison.Ordinal);
            var (k, v) = eq < 0
                ? (Uri.UnescapeDataString(segment), string.Empty)
                : (Uri.UnescapeDataString(segment[..eq]), Uri.UnescapeDataString(segment[(eq + 1)..]));
            pairs.Add(new(k, v));
        }
        return Validate(pairs);
    }
}
