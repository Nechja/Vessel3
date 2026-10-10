namespace Vessel3.Server.S3.Key;

internal sealed class AbortMultipartUpload(IMultipartStore multipart, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Delete, S3KeySubresource.UploadId);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var uploadId = ctx.Request.Query.TryGetValue("uploadId", out var val) && val.Count > 0 ? val[0] ?? string.Empty : string.Empty;
        var result = multipart.Abort(uploadId);
        if (result is Result.Failure f) return Task.FromResult(http.Map(f.Error));
        return Task.FromResult<IResult>(Results.NoContent());
    }
}
