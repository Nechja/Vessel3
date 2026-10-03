using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class MkcolAction(
    IBucketRegistry registry,
    IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Mkcol;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return new WebDavErrorResult(new InvalidRequestError("Bucket is required for MKCOL"));
        }

        var caller = ctx.GetCaller();

        return string.IsNullOrEmpty(target.Path)
            ? CreateBucket(target.Bucket, caller, ctx.Response)
            : await CreateDirectoryCollection(target.Bucket, target.Path, ctx.Response, ctx.RequestAborted);
    }

    private IResult CreateBucket(string bucket, CallerIdentity caller, HttpResponse res)
    {
        var createRes = registry.Create(bucket, caller);
        if (!createRes.TryGetValue(out _, out var err))
        {
            return new WebDavErrorResult(err);
        }

        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private async Task<IResult> CreateDirectoryCollection(string bucket, string path, HttpResponse res, CancellationToken ct)
    {
        var bucketRes = registry.Exists(bucket);
        if (!bucketRes.TryGetValue(out var exists, out var bErr) || !exists)
        {
            return new WebDavErrorResult(bErr ?? new NoSuchBucketError(bucket));
        }

        var dirKey = path.TrimStart('/').TrimEnd('/') + "/";
        var putReq = new ObjectPutRequest(
            Bucket: bucket,
            Key: dirKey,
            Body: Stream.Null,
            DeclaredSize: 0,
            ContentType: WebDavMediaTypes.Directory,
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: FrozenDictionary<string, string>.Empty,
            Tags: FrozenDictionary<string, string>.Empty,
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: ct);

        var putRes = await objects.Put(putReq);
        if (!putRes.TryGetValue(out _, out var putErr))
        {
            return new WebDavErrorResult(putErr);
        }

        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;
        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
