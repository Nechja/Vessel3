using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class MoveAction(
    IBucketLister lister,
    IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Move;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket) || string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new InvalidRequestError("Source bucket and key path are required for MOVE"));
        }

        if (string.IsNullOrEmpty(target.Destination))
        {
            return new WebDavErrorResult(new InvalidRequestError("Destination header is required for MOVE"));
        }

        var (destinationBucket, destinationKey) = WebDavRequestParser.ParseDestination(target.Destination);
        if (string.IsNullOrEmpty(destinationBucket) || string.IsNullOrEmpty(destinationKey))
        {
            return new WebDavErrorResult(new InvalidRequestError("Invalid Destination URI"));
        }

        var sourceKey = target.Path.TrimStart('/');
        var cleanDestinationKey = destinationKey.TrimStart('/');

        var destinationExists = objects.Stat(destinationBucket, cleanDestinationKey) is Result<ObjectStat>.Success;
        if (destinationExists && !target.Overwrite)
        {
            return new WebDavErrorResult(new PreconditionFailedError($"Destination {destinationBucket}/{cleanDestinationKey} already exists and Overwrite is F"));
        }

        var transferResult = await TransferResource(target.Bucket, sourceKey, destinationBucket, cleanDestinationKey);
        if (transferResult is Result.Failure failure)
        {
            return new WebDavErrorResult(failure.Error);
        }

        ctx.Response.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        ctx.Response.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;

        return Results.StatusCode(destinationExists ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
    }

    private async Task<Result> TransferResource(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey)
    {
        var copyResult = await objects.Copy(destinationBucket, destinationKey, sourceBucket, sourceKey);
        if (copyResult is Result<CopyOutcome>.Success)
        {
            objects.Delete(sourceBucket, sourceKey);
            return Result.Ok;
        }

        var sourcePrefix = sourceKey.TrimEnd('/') + "/";
        var destinationPrefix = destinationKey.TrimEnd('/') + "/";
        return await MoveCollection(sourceBucket, sourcePrefix, destinationBucket, destinationPrefix);
    }

    private async Task<Result> MoveCollection(string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix)
    {
        var listRequest = new ListRequest(sourceBucket, sourcePrefix, null, null, 1000);
        string? continuationToken = null;
        var hasMovedAny = false;

        do
        {
            var listResult = lister.List(listRequest, continuationToken);
            if (!listResult.TryGetValue(out var page, out var error))
            {
                return hasMovedAny ? Result.Ok : error;
            }

            foreach (var entry in page.Entries)
            {
                var relativePath = entry.Key[sourcePrefix.Length..];
                var itemDestinationKey = destinationPrefix + relativePath;
                if (await objects.Copy(destinationBucket, itemDestinationKey, sourceBucket, entry.Key) is Result<CopyOutcome>.Success)
                {
                    objects.Delete(sourceBucket, entry.Key);
                    hasMovedAny = true;
                }
            }

            continuationToken = page.NextContinuationToken;
        } while (!string.IsNullOrEmpty(continuationToken));

        if (!hasMovedAny)
        {
            return new NoSuchKeyError(sourcePrefix);
        }

        objects.Delete(sourceBucket, sourcePrefix);
        return Result.Ok;
    }
}
