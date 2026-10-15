using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class GetBlobAction(IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Get;
    public BucketCapability RequiredCapability => BucketCapability.Read;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return new WebDavErrorResult(new InvalidRequestError("Bucket is required for GET"));
        }

        if (string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new MethodNotAllowedError("Cannot GET a bucket collection directly"));
        }

        var cleanPath = target.Path.TrimStart('/');
        var getRes = await objects.Get(target.Bucket, cleanPath, ct: ctx.RequestAborted);
        if (!getRes.TryGetValue(out var obj, out var err))
        {
            return new WebDavErrorResult(err);
        }

        var res = ctx.Response;
        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;

        var etagHeader = obj.Etag.StartsWith('"') ? obj.Etag : $"\"{obj.Etag}\"";

        var hasRange = ctx.Request.Headers.TryGetValue("Range", out var rangeVal) && !string.IsNullOrEmpty(rangeVal);
        if (!hasRange)
        {
            res.ContentType = obj.ContentType;
            res.ContentLength = obj.Size;
            res.Headers.ETag = etagHeader;
            res.Headers.LastModified = WebDavXmlDefaults.ToRfc1123(obj.LastModified);
            res.Headers.AcceptRanges = "bytes";

            await using (obj.Body)
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(80 * 1024);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await obj.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted)) > 0)
                    {
                        await res.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return Results.Empty;
        }

        return Results.Stream(
            obj.Body,
            contentType: obj.ContentType,
            lastModified: obj.LastModified,
            entityTag: new EntityTagHeaderValue(etagHeader),
            enableRangeProcessing: true);
    }
}
