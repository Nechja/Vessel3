namespace Vessel3.Storage;

internal enum BucketCapability
{
    Read = 0,
    Write = 1,
    Admin = 2,
}

internal static class BucketPolicy
{
    public static bool Allows(CallerIdentity? caller, Bucket bucket, BucketCapability capability) =>
        Allows(caller, bucket.GetOwner(), bucket.Access, capability);

    public static bool Allows(CallerIdentity? caller, string? ownerId, BucketAccess access, BucketCapability capability) =>
        (capability is BucketCapability.Read && access.PublicRead)
        || (caller is not null && (caller.IsAdmin || ((capability is BucketCapability.Read || caller.CanWrite)
            && !(capability is BucketCapability.Write && access.ReadOnly)
            && string.Equals(ownerId, caller.UserId, StringComparison.Ordinal))));

    public static Result Authorize(CallerIdentity? caller, Bucket bucket, BucketCapability capability) =>
        Allows(caller, bucket, capability)
            ? Result.Ok
            : new AccessDeniedError($"Caller is not authorized for {capability} on bucket '{bucket.Name}'");

    public static Result Authorize(CallerIdentity? caller, string? ownerId, BucketAccess access, string bucketName, BucketCapability capability) =>
        Allows(caller, ownerId, access, capability)
            ? Result.Ok
            : new AccessDeniedError($"Caller is not authorized for {capability} on bucket '{bucketName}'");
}
