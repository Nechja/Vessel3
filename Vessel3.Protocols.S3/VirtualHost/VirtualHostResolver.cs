namespace Vessel3.Server.S3;

internal interface IVirtualHostResolver
{
    bool IsAdminHost(string? hostHeader);
    bool TryExtractBucket(string? hostHeader, out string bucket);
}

internal sealed class VirtualHostResolver(IBucketRegistry registry, VirtualHostOptions options) : IVirtualHostResolver
{
    public bool IsAdminHost(string? hostHeader) =>
        options.BaseDomains.Count > 0 && VirtualHostParser.IsAdminHost(hostHeader, options.BaseDomains);

    public bool TryExtractBucket(string? hostHeader, out string bucket) =>
        VirtualHostParser.TryExtractBucket(hostHeader, options.BaseDomains, registry, out bucket);
}
