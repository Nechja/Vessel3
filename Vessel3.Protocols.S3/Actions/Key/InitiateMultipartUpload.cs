using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class InitiateMultipartUpload(IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Post, S3KeySubresource.Uploads);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
        Task.FromResult(multipart.Create(bucket, key, ctx.Request.ContentType, ExtractUserMetadata(ctx.Request.Headers)).Match<IResult>(
            outcome =>
            {
                ctx.Response.ContentType = "application/xml";
                return Results.Stream(async stream =>
                    await xml.WriteInitiateMultipartUploadResult(stream, bucket, key, outcome.UploadId, ctx.RequestAborted),
                    "application/xml");
            },
            http.Map));
}
