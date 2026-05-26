namespace Vessel3.Server.S3;

internal enum S3Subresource
{
    None,
    Location,
    Uploads,
    Versioning,
    ObjectLock,
    Lifecycle,
    Versions,
    Delete,
}

internal static class S3SubresourceParser
{
    public static S3Subresource From(IQueryCollection query) =>
        query.ContainsKey("location") ? S3Subresource.Location
        : query.ContainsKey("uploads") ? S3Subresource.Uploads
        : query.ContainsKey("versioning") ? S3Subresource.Versioning
        : query.ContainsKey("object-lock") ? S3Subresource.ObjectLock
        : query.ContainsKey("lifecycle") ? S3Subresource.Lifecycle
        : query.ContainsKey("versions") ? S3Subresource.Versions
        : query.ContainsKey("delete") ? S3Subresource.Delete
        : S3Subresource.None;
}
