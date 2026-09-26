namespace Vessel3.Server.S3;

internal enum S3KeySubresource
{
    None,
    Uploads,
    UploadId,
    Tagging,
    Retention,
    LegalHold,
    Attributes,
}

internal static class S3KeySubresourceParser
{
    public static S3KeySubresource From(IQueryCollection query) =>
        query.ContainsKey("uploads") ? S3KeySubresource.Uploads
        : !string.IsNullOrEmpty(query["uploadId"].ToString()) ? S3KeySubresource.UploadId
        : query.ContainsKey("tagging") ? S3KeySubresource.Tagging
        : query.ContainsKey("retention") ? S3KeySubresource.Retention
        : query.ContainsKey("legal-hold") ? S3KeySubresource.LegalHold
        : query.ContainsKey("attributes") ? S3KeySubresource.Attributes
        : S3KeySubresource.None;
}
