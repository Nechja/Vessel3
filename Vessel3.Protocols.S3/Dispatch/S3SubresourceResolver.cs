namespace Vessel3.Server.S3;

internal interface IS3SubresourceResolver
{
    S3BucketSubresource ResolveBucket(IQueryCollection query);
    S3KeySubresource ResolveKey(IQueryCollection query);
}

internal sealed class S3SubresourceResolver : IS3SubresourceResolver
{
    private readonly record struct BucketRule(string Parameter, S3BucketSubresource Subresource);
    private readonly record struct KeyRule(string Parameter, S3KeySubresource Subresource, bool RequiresValue);

    private static readonly BucketRule[] BucketRules =
    [
        new("location",    S3BucketSubresource.Location),
        new("uploads",     S3BucketSubresource.Uploads),
        new("versioning",  S3BucketSubresource.Versioning),
        new("object-lock", S3BucketSubresource.ObjectLock),
        new("lifecycle",   S3BucketSubresource.Lifecycle),
        new("versions",    S3BucketSubresource.Versions),
        new("delete",      S3BucketSubresource.Delete),
        new("website",     S3BucketSubresource.Website),
        new("cors",        S3BucketSubresource.Cors),
        new("acl",         S3BucketSubresource.Acl),
    ];

    private static readonly KeyRule[] KeyRules =
    [
        new("uploads",    S3KeySubresource.Uploads,    false),
        new("uploadId",   S3KeySubresource.UploadId,   true),
        new("tagging",    S3KeySubresource.Tagging,    false),
        new("retention",  S3KeySubresource.Retention,  false),
        new("legal-hold", S3KeySubresource.LegalHold,  false),
        new("attributes", S3KeySubresource.Attributes, false),
    ];

    public S3BucketSubresource ResolveBucket(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return S3BucketSubresource.None;
        }

        foreach (ref readonly var rule in BucketRules.AsSpan())
        {
            if (query.ContainsKey(rule.Parameter))
            {
                return rule.Subresource;
            }
        }

        return S3BucketSubresource.None;
    }

    public S3KeySubresource ResolveKey(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return S3KeySubresource.None;
        }

        foreach (ref readonly var rule in KeyRules.AsSpan())
        {
            if (!query.TryGetValue(rule.Parameter, out var value))
            {
                continue;
            }

            if (rule.RequiresValue && string.IsNullOrEmpty(value.ToString()))
            {
                continue;
            }

            return rule.Subresource;
        }

        return S3KeySubresource.None;
    }
}
