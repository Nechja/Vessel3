using System.Globalization;

namespace Vessel3.Server.S3;

internal abstract record S3ByteRange
{
    internal sealed record Normal(long Start, long End) : S3ByteRange;
    internal sealed record Unsatisfiable : S3ByteRange;
    internal sealed record Ignored : S3ByteRange;

    public static bool TryParse(string? raw, long size, out long start, out long end)
    {
        if (Parse(raw, size) is Normal n)
        {
            start = n.Start;
            end = n.End;
            return true;
        }

        start = 0;
        end = 0;
        return false;
    }

    public static S3ByteRange Parse(string? raw, long size)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new Ignored();
        }

        const string prefix = "bytes=";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new Ignored();
        }

        var rest = raw[prefix.Length..].Trim();
        if (rest.Contains(',', StringComparison.Ordinal))
        {
            return new Ignored();
        }

        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return new Ignored();
        }

        var startStr = rest[..dash];
        var endStr = rest[(dash + 1)..];

        if (string.IsNullOrEmpty(startStr))
        {
            if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            {
                return new Ignored();
            }

            if (n is 0 || size is 0)
            {
                return new Unsatisfiable();
            }

            var clamped = Math.Min(n, size);
            return new Normal(size - clamped, size - 1);
        }

        if (!long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) || start < 0)
        {
            return new Ignored();
        }

        if (start >= size)
        {
            return new Unsatisfiable();
        }

        if (string.IsNullOrEmpty(endStr))
        {
            return new Normal(start, size - 1);
        }

        if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) || end < start)
        {
            return new Ignored();
        }

        if (end >= size)
        {
            end = size - 1;
        }

        return new Normal(start, end);
    }
}
