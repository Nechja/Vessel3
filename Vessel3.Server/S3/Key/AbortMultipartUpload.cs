namespace Vessel3.Server.S3.Key;

internal sealed class AbortMultipartUpload(IMultipartStore multipart, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Delete, S3KeySubresource.UploadId);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
        Task.FromResult(multipart.Abort(ctx.Request.Query["uploadId"].ToString()).Match<IResult>(_ => Results.NoContent(), http.Map));
}
