using static Vessel3.Server.S3.S3RequestExtensions;

namespace Vessel3.Server.S3.Bucket;

internal static class ListObjectsQuery
{
    public static (S3ListObjectsRequest Request, string? ContinuationToken) Bind(string bucket, IQueryCollection query)
    {
        var prefix = query.TryGetValue("prefix", out var pVal) ? Nullify(pVal) : null;
        var delimiter = query.TryGetValue("delimiter", out var dVal) ? Nullify(dVal) : null;
        var maxKeys = query.TryGetValue("max-keys", out var mkVal) && int.TryParse(mkVal.ToString(), out var mk) ? mk : (int?)null;
        var continuationToken = query.TryGetValue("continuation-token", out var ctVal) ? Nullify(ctVal) : null;
        var startAfter = query.TryGetValue("start-after", out var saVal) ? Nullify(saVal) : null;
        var marker = query.TryGetValue("marker", out var mVal) ? Nullify(mVal) : null;
        var listType = query.TryGetValue("list-type", out var ltVal) ? Nullify(ltVal) : null;
        var encodingType = query.TryGetValue("encoding-type", out var etVal) ? Nullify(etVal) : null;

        var isV1 = listType is not "2";
        var effectiveStart = isV1 ? marker : startAfter;
        var req = new S3ListObjectsRequest(
            bucket, prefix, delimiter, effectiveStart,
            Math.Clamp(maxKeys ?? 1000, 1, 1000),
            IsV1: isV1, Marker: marker, EncodingType: encodingType);

        return (req, isV1 ? null : continuationToken);
    }
}
