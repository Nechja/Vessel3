namespace Vessel3.Server.S3;

internal enum S3BucketSubresource
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

internal static class S3BucketSubresourceParser
{
    public static S3BucketSubresource From(IQueryCollection query) =>
        query.ContainsKey("location") ? S3BucketSubresource.Location
        : query.ContainsKey("uploads") ? S3BucketSubresource.Uploads
        : query.ContainsKey("versioning") ? S3BucketSubresource.Versioning
        : query.ContainsKey("object-lock") ? S3BucketSubresource.ObjectLock
        : query.ContainsKey("lifecycle") ? S3BucketSubresource.Lifecycle
        : query.ContainsKey("versions") ? S3BucketSubresource.Versions
        : query.ContainsKey("delete") ? S3BucketSubresource.Delete
        : S3BucketSubresource.None;
}
