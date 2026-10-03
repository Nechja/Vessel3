using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class DeleteAction(
    IBucketRegistry registry,
    IBucketLister lister,
    IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Delete;

    public Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return Task.FromResult<IResult>(new WebDavErrorResult(new InvalidRequestError("Bucket is required for DELETE")));
        }

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        var result = string.IsNullOrEmpty(target.Path)
            ? DeleteBucket(target.Bucket, caller)
            : DeleteResource(target.Bucket, target.Path);

        return Task.FromResult(result);
    }

    private IResult DeleteBucket(string bucket, CallerIdentity caller)
    {
        var delRes = registry.Delete(bucket, caller);
        return delRes.TryGetError(out var err)
            ? new WebDavErrorResult(err)
            : Results.NoContent();
    }

    private IResult DeleteResource(string bucket, string path)
    {
        var cleanPath = path.TrimStart('/');
        objects.Delete(bucket, cleanPath);

        var markerKey = cleanPath.TrimEnd('/') + "/";
        objects.Delete(bucket, markerKey);

        DeleteCollectionChildren(bucket, markerKey);
        return Results.NoContent();
    }

    private void DeleteCollectionChildren(string bucket, string prefix)
    {
        var listReq = new ListRequest(bucket, prefix, null, null, 1000);
        string? token = null;

        do
        {
            var listRes = lister.List(listReq, token);
            if (!listRes.TryGetValue(out var page, out _))
            {
                break;
            }

            foreach (var entry in page.Entries)
            {
                objects.Delete(bucket, entry.Key);
            }

            token = page.NextContinuationToken;
        } while (!string.IsNullOrEmpty(token));
    }
}
