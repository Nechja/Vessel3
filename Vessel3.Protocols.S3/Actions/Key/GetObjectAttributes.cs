namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectAttributes(IObjectStore objects, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.Attributes);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var fields = ctx.Request.Headers["x-amz-object-attributes"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var res = objects.GetAttributes(bucket, key, ctx.VersionId());
        if (!res.TryGetValue(out var data, out var err))
        {
            return http.Map(err);
        }

        var sha = string.IsNullOrEmpty(data.Sha256)
            ? null
            : Convert.ToBase64String(Convert.FromHexString(data.Sha256));
        var attrReq = new ObjectAttributesRequest(
            WantEtag: fields.Contains("ETag"), Etag: data.Etag,
            WantChecksum: fields.Contains("Checksum"), ChecksumSha256Base64: sha,
            WantObjectParts: fields.Contains("ObjectParts"), Parts: data.Parts,
            WantStorageClass: fields.Contains("StorageClass"),
            WantObjectSize: fields.Contains("ObjectSize"), Size: data.Size);
        ctx.Response.ContentType = "application/xml";
        await xml.WriteObjectAttributes(ctx.Response.Body, attrReq, ctx.RequestAborted);
        return Results.Empty;
    }
}
