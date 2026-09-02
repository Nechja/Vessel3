namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteObjects(IObjectStore objects, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Post, S3BucketSubresource.Delete);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        if (!(await reader.ReadBatchDeleteRequest(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var request, out var err))
            return http.Map(err);

        var bypass = ctx.BypassGovernanceRetention();
        var items = new List<Storage.BatchDeleteItem>(request.Keys.Count);
        foreach (var k in request.Keys)
            items.Add(new Storage.BatchDeleteItem(k.Key, string.IsNullOrEmpty(k.VersionId) ? null : k.VersionId, bypass));

        var outcomes = new List<BatchDeleteOutcome>(request.Keys.Count);
        if (objects.DeleteBatch(bucket, items).TryGetValue(out var results, out var batchErr))
            for (var i = 0; i < request.Keys.Count; i++)
                outcomes.Add(results[i] is Result<Storage.DeleteOutcome>.Failure df
                    ? new BatchDeleteOutcome(request.Keys[i].Key, request.Keys[i].VersionId, df.Error)
                    : new BatchDeleteOutcome(request.Keys[i].Key, request.Keys[i].VersionId, null));
        else
            foreach (var k in request.Keys)
                outcomes.Add(new BatchDeleteOutcome(k.Key, k.VersionId, batchErr));

        ctx.Response.ContentType = "application/xml";
        return Results.Stream(
            async stream => await xml.WriteBatchDeleteResult(stream, outcomes, request.Quiet, ctx.RequestAborted),
            "application/xml");
    }
}
