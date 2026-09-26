using static Vessel3.Server.S3.S3RequestExtensions;

namespace Vessel3.Server.S3.Bucket;

internal static class ListObjectsQuery
{
    public static (ListRequest Request, string? ContinuationToken) Bind(string bucket, IQueryCollection query)
    {
        var prefix = Nullify(query["prefix"].ToString());
        var delimiter = Nullify(query["delimiter"].ToString());
        var maxKeys = int.TryParse(query["max-keys"].ToString(), out var mk) ? mk : (int?)null;
        var continuationToken = Nullify(query["continuation-token"].ToString());
        var startAfter = Nullify(query["start-after"].ToString());
        var marker = Nullify(query["marker"].ToString());
        var listType = Nullify(query["list-type"].ToString());
        var encodingType = Nullify(query["encoding-type"].ToString());

        var isV1 = listType is not "2";
        var effectiveStart = isV1 ? marker : startAfter;
        var req = new ListRequest(
            bucket, prefix, delimiter, effectiveStart,
            Math.Clamp(maxKeys ?? 1000, 1, 1000),
            IsV1: isV1, Marker: marker, EncodingType: encodingType);

        return (req, isV1 ? null : continuationToken);
    }
}
