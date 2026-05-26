namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteObjects(IObjectStore objects, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Post, S3BucketSubresource.Delete);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var parsed = await reader.ReadBatchDeleteRequest(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<BatchDeleteRequest>.Failure f) return http.Map(f.Error);

        var request = ((Result<BatchDeleteRequest>.Success)parsed).Value;
        var batchBypass = ctx.Request.Headers["x-amz-bypass-governance-retention"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var outcomes = new List<BatchDeleteOutcome>(request.Keys.Count);
        foreach (var k in request.Keys)
        {
            var result = string.IsNullOrEmpty(k.VersionId)
                ? objects.Delete(bucket, k.Key, batchBypass)
                : objects.DeleteVersion(bucket, k.Key, k.VersionId, batchBypass);
            outcomes.Add(result is Result<Storage.DeleteOutcome>.Failure df
                ? new BatchDeleteOutcome(k.Key, k.VersionId, df.Error)
                : new BatchDeleteOutcome(k.Key, k.VersionId, null));
        }

        ctx.Response.ContentType = "application/xml";
        return Results.Stream(
            async stream => await xml.WriteBatchDeleteResult(stream, outcomes, request.Quiet, ctx.RequestAborted),
            "application/xml");
    }
}
