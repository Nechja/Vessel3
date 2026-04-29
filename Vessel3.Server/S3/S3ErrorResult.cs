namespace Vessel3.Server.S3;

internal sealed class S3ErrorResult(Error error, IS3XmlWriter xml) : IResult
{
    public async Task ExecuteAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = error.Status;
        ctx.Response.ContentType = "application/xml";
        await xml.WriteError(
            ctx.Response.Body,
            error,
            ctx.Request.Path,
            Guid.NewGuid().ToString("N"),
            ctx.RequestAborted);
    }
}
