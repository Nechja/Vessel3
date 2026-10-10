using System.Collections.Frozen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Headers;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class ObjectEndpoints
{
    public static IEndpointRouteBuilder MapObjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets/{bucket}/objects", ListObjects);
        endpoints.MapGet("/v1/buckets/{bucket}/objects/{**key}", GetObject);
        endpoints.MapMethods("/v1/buckets/{bucket}/objects/{**key}", ["HEAD"], HeadObject);
        endpoints.MapPut("/v1/buckets/{bucket}/objects/{**key}", PutObject);
        endpoints.MapDelete("/v1/buckets/{bucket}/objects/{**key}", DeleteObject);
        return endpoints;
    }

    private static IResult ListObjects(
        string bucket,
        HttpContext context,
        IBucketLister lister,
        IBucketRegistry registry)
    {
        RequestTrace.SetTarget("ListObjects", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var prefix = NativeHeaderCodec.Nullify(context.Request.Query["prefix"]);
        var marker = NativeHeaderCodec.Nullify(context.Request.Query["marker"]);
        var delimiter = NativeHeaderCodec.Nullify(context.Request.Query["delimiter"]);
        var limit = int.TryParse(context.Request.Query["limit"], out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 1000;

        var result = lister.List(new ListRequest(bucket, prefix, delimiter, marker, limit), continuationToken: null);
        if (!result.TryGetValue(out var page, out var error))
        {
            return error.ToHttpResult();
        }

        var objectsList = new List<ObjectSummaryDto>(page.Entries.Count);
        var prefixes = new List<string>();
        foreach (var entry in page.Entries)
        {
            if (entry is ListEntry.Contents contents)
                objectsList.Add(new ObjectSummaryDto(contents.Key, contents.Size, contents.Etag, contents.LastModified, null));
            else if (entry is ListEntry.CommonPrefix prefixEntry)
                prefixes.Add(prefixEntry.Key);
        }
        var nextMarker = page.NextContinuationToken ?? page.LastKey;

        var dto = new ObjectsPageDto(objectsList, prefixes, page.IsTruncated, nextMarker);
        return Results.Json(dto, NativeJsonContext.Default.ObjectsPageDto);
    }

    private static async Task<IResult> GetObject(
        string bucket,
        string key,
        HttpContext context,
        IObjectStore objects,
        IBucketRegistry registry)
    {
        RequestTrace.SetTarget("GetObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = await objects.Get(bucket, key, versionId, context.RequestAborted);
        if (!result.TryGetValue(out var storedObject, out var error))
        {
            return error.ToHttpResult();
        }

        var res = context.Response;
        NativeHeaderCodec.ApplyObjectHeaders(res.Headers, storedObject, versionId);
        res.ContentType = storedObject.ContentType ?? "application/octet-stream";
        res.ContentLength = storedObject.Size;

        await using (storedObject.Body)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                while (true)
                {
                    var read = await storedObject.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted);
                    if (read == 0) break;
                    await res.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return Results.Empty;
    }

    private static IResult HeadObject(
        string bucket,
        string key,
        HttpContext context,
        IObjectStore objects,
        IBucketRegistry registry)
    {
        RequestTrace.SetTarget("HeadObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = objects.Stat(bucket, key, versionId);
        if (!result.TryGetValue(out var stat, out var error))
        {
            return error.ToHttpResult();
        }

        NativeHeaderCodec.ApplyObjectHeaders(context.Response.Headers, stat, versionId);
        return Results.Ok();
    }

    private static async Task<IResult> PutObject(
        string bucket,
        string key,
        HttpContext context,
        IObjectStore objects,
        IBucketRegistry registry)
    {
        RequestTrace.SetTarget("PutObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var metadata = NativeHeaderCodec.ExtractMetadata(context.Request.Headers);

        var putRequest = new ObjectPutRequest(
            bucket,
            key,
            context.Request.Body,
            context.Request.ContentLength,
            context.Request.ContentType,
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: metadata,
            Tags: FrozenDictionary<string, string>.Empty,
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: context.RequestAborted,
            Protocol: "native",
            Actor: caller?.Username,
            Host: context.Request.Host.Value);

        var result = await objects.Put(putRequest);
        if (!result.TryGetValue(out var outcome, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new PutObjectResultDto(outcome.Etag, outcome.VersionId, outcome.Size, outcome.Sha256), NativeJsonContext.Default.PutObjectResultDto);
    }

    private static IResult DeleteObject(
        string bucket,
        string key,
        HttpContext context,
        IObjectStore objects,
        IBucketRegistry registry)
    {
        RequestTrace.SetTarget("DeleteObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = versionId is not null
            ? objects.DeleteVersion(bucket, key, versionId)
            : objects.Delete(bucket, key);

        if (!result.TryGetValue(out _, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.NoContent();
    }
}
