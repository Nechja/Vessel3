using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Protocols.Native.Headers;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class ObjectEndpoints
{
    public static IEndpointRouteBuilder MapObjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets/{bucket}/objects", static (
            string bucket,
            HttpContext ctx,
            IBucketLister lister,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var prefix = NativeHeaderCodec.Nullify(ctx.Request.Query["prefix"].ToString());
            var marker = NativeHeaderCodec.Nullify(ctx.Request.Query["marker"].ToString());
            var delimiter = NativeHeaderCodec.Nullify(ctx.Request.Query["delimiter"].ToString());
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) && l > 0 ? l : 1000;

            var result = lister.List(new ListRequest(bucket, prefix, delimiter, marker, limit), continuationToken: null);
            if (!result.TryGetValue(out var page, out var err))
            {
                return err.ToHttpResult();
            }

            List<ObjectSummaryDto> objectsList = [.. page.Entries.OfType<ListEntry.Contents>()
                .Select(c => new ObjectSummaryDto(c.Key, c.Size, c.Etag, c.LastModified, null))];
            List<string> prefixes = [.. page.Entries.OfType<ListEntry.CommonPrefix>()
                .Select(p => p.Key)];
            var nextMarker = page.NextContinuationToken ?? page.LastKey;

            var dto = new ObjectsPageDto(objectsList, prefixes, page.IsTruncated, nextMarker);
            return Results.Json(dto, NativeJsonContext.Default.ObjectsPageDto);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/objects/{**key}", static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var versionId = NativeHeaderCodec.Nullify(ctx.Request.Query["versionId"].ToString());
            var result = objects.Get(bucket, key, versionId);
            if (!result.TryGetValue(out var obj, out var err))
            {
                return err.ToHttpResult();
            }

            var currentVersionId = registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) ? cur?.VersionId : null;
            NativeHeaderCodec.ApplyObjectHeaders(ctx.Response.Headers, obj, versionId, currentVersionId);

            return Results.Stream(obj.Body, obj.ContentType ?? "application/octet-stream");
        });

        endpoints.MapMethods("/v1/buckets/{bucket}/objects/{**key}", ["HEAD"], static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var versionId = NativeHeaderCodec.Nullify(ctx.Request.Query["versionId"].ToString());
            var result = objects.Stat(bucket, key, versionId);
            if (!result.TryGetValue(out var stat, out var err))
            {
                return err.ToHttpResult();
            }

            var currentVersionId = registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) ? cur?.VersionId : null;
            NativeHeaderCodec.ApplyObjectHeaders(ctx.Response.Headers, stat, versionId, currentVersionId);

            return Results.Ok();
        });

        endpoints.MapPut("/v1/buckets/{bucket}/objects/{**key}", static async (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var metadata = NativeHeaderCodec.ExtractMetadata(ctx.Request.Headers);

            var result = await objects.Put(
                bucket,
                key,
                ctx.Request.Body,
                ctx.Request.ContentLength,
                ctx.Request.ContentType,
                declaredSha256: null,
                declaredMd5Base64: null,
                metadata,
                tags: new Dictionary<string, string>(),
                declaredChecksums: ChecksumSet.Empty,
                ctx.RequestAborted);

            return result.Match(
                res => Results.Json(new PutObjectResultDto(res.Etag, res.VersionId, res.Size, res.Sha256), NativeJsonContext.Default.PutObjectResultDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapDelete("/v1/buckets/{bucket}/objects/{**key}", static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var versionId = NativeHeaderCodec.Nullify(ctx.Request.Query["versionId"].ToString());
            var result = versionId is not null
                ? objects.DeleteVersion(bucket, key, versionId)
                : objects.Delete(bucket, key);

            return result.Match(_ => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        return endpoints;
    }
}
