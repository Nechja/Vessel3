using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class CopyAction(
    IBucketLister lister,
    IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Copy;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket) || string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new InvalidRequestError("Source bucket and key path are required for COPY"));
        }

        if (string.IsNullOrEmpty(target.Destination))
        {
            return new WebDavErrorResult(new InvalidRequestError("Destination header is required for COPY"));
        }

        var (destBucket, destKey) = WebDavRequestParser.ParseDestination(target.Destination);
        if (string.IsNullOrEmpty(destBucket) || string.IsNullOrEmpty(destKey))
        {
            return new WebDavErrorResult(new InvalidRequestError("Invalid Destination URI"));
        }

        var srcKey = target.Path.TrimStart('/');
        var cleanDestKey = destKey.TrimStart('/');

        var destExists = objects.Stat(destBucket, cleanDestKey) is Result<ObjectStat>.Success;
        if (destExists && !target.Overwrite)
        {
            return new WebDavErrorResult(new PreconditionFailedError($"Destination {destBucket}/{cleanDestKey} already exists and Overwrite is F"));
        }

        var copyRes = await objects.Copy(destBucket, cleanDestKey, target.Bucket, srcKey);
        if (copyRes is Result<CopyOutcome>.Failure copyFailure)
        {
            var srcPrefix = srcKey.TrimEnd('/') + "/";
            var destPrefix = cleanDestKey.TrimEnd('/') + "/";
            var collectionRes = await CopyCollection(target.Bucket, srcPrefix, destBucket, destPrefix);
            if (collectionRes.TryGetError(out _))
            {
                return new WebDavErrorResult(copyFailure.Error);
            }
        }

        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";

        return Results.StatusCode(destExists ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
    }

    private async Task<Result> CopyCollection(string srcBucket, string srcPrefix, string destBucket, string destPrefix)
    {
        var listReq = new ListRequest(srcBucket, srcPrefix, null, null, 1000);
        string? token = null;
        var anyCopied = false;

        do
        {
            var listRes = lister.List(listReq, token);
            if (!listRes.TryGetValue(out var page, out var err))
            {
                return anyCopied ? Result.Ok : err;
            }

            foreach (var entry in page.Entries)
            {
                var relative = entry.Key[srcPrefix.Length..];
                var itemDestKey = destPrefix + relative;
                var itemRes = await objects.Copy(destBucket, itemDestKey, srcBucket, entry.Key);
                if (itemRes is Result<CopyOutcome>.Success)
                {
                    anyCopied = true;
                }
            }

            token = page.NextContinuationToken;
        } while (!string.IsNullOrEmpty(token));

        return anyCopied ? Result.Ok : new NoSuchKeyError(srcPrefix);
    }
}
