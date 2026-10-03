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

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

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

        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";
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
            ContentType: "application/x-directory",
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: new Dictionary<string, string>(),
            Tags: new Dictionary<string, string>(),
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: ct);

        var putRes = await objects.Put(putReq);
        if (!putRes.TryGetValue(out _, out var putErr))
        {
            return new WebDavErrorResult(putErr);
        }

        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";
        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
