using static Vessel3.Server.S3.S3RequestExtensions;

namespace Vessel3.Server.S3.Bucket;

internal readonly record struct ListObjectVersionsQuery(string? Prefix, string? KeyMarker, int MaxKeys, string? EncodingType)
{
    public static ListObjectVersionsQuery Bind(IQueryCollection query)
    {
        var prefix = Nullify(query["prefix"].ToString());
        var encodingType = Nullify(query["encoding-type"].ToString());
        var keyMarker = Nullify(query["key-marker"].ToString());
        var maxKeys = Math.Clamp(int.TryParse(query["max-keys"].ToString(), out var mk) ? mk : 1000, 1, 1000);
        return new(prefix, keyMarker, maxKeys, encodingType);
    }
}
