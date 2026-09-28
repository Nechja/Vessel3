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

    public static bool Allows(CallerIdentity? caller, string? ownerId, BucketAccess access, BucketCapability capability)
    {
        if (capability is BucketCapability.Write && access.ReadOnly)
            return false;

        if (capability is BucketCapability.Read && access.PublicRead)
            return true;

        if (caller is null)
            return false;

        if (caller.IsAdmin)
            return true;

        return (capability is BucketCapability.Read || caller.CanWrite)
            && string.Equals(ownerId, caller.UserId, StringComparison.Ordinal);
    }

    public static Result Authorize(CallerIdentity? caller, Bucket bucket, BucketCapability capability) =>
        Authorize(caller, bucket.GetOwner(), bucket.Access, bucket.Name, capability);

    public static Result Authorize(CallerIdentity? caller, string? ownerId, BucketAccess access, string bucketName, BucketCapability capability)
    {
        if (capability is BucketCapability.Write && access.ReadOnly)
            return new BucketIsReadOnlyError(bucketName);

        return Allows(caller, ownerId, access, capability)
            ? Result.Ok
            : new AccessDeniedError($"Caller is not authorized for {capability} on bucket '{bucketName}'");
    }
}
