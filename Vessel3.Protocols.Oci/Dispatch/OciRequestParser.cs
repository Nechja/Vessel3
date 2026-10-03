using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Oci.Dispatch;

internal static class OciRequestParser
{
    public static OciRequestTarget Parse(string path, HttpContext ctx)
    {
        var cleanPath = path.Trim('/');
        var method = ctx.Request.Method;

        return cleanPath switch
        {
            "" => new(OciOperationKind.Ping),
            _ when cleanPath.Equals("v2", StringComparison.OrdinalIgnoreCase) => new(OciOperationKind.Ping),
            _ when cleanPath.Equals("token", StringComparison.OrdinalIgnoreCase) => new(OciOperationKind.Token),
            _ when cleanPath.Equals("_catalog", StringComparison.OrdinalIgnoreCase) => new(OciOperationKind.Catalog),
            _ when cleanPath.Contains("tags/list", StringComparison.OrdinalIgnoreCase) => ResolveTags(cleanPath),
            _ when cleanPath.Contains("blobs/uploads", StringComparison.OrdinalIgnoreCase) => ResolveBlobUpload(cleanPath, method),
            _ when cleanPath.Contains("/blobs/", StringComparison.OrdinalIgnoreCase) => ResolveBlob(cleanPath, method),
            _ when cleanPath.Contains("/manifests/", StringComparison.OrdinalIgnoreCase) => ResolveManifest(cleanPath, method),
            _ => new(OciOperationKind.Unknown)
        };
    }

    private static OciRequestTarget ResolveTags(string path)
    {
        var idx = path.IndexOf("/tags/list", StringComparison.OrdinalIgnoreCase);
        var repo = idx >= 0 ? path[..idx] : path;
        return new(OciOperationKind.Tags, Repo: repo);
    }

    private static OciRequestTarget ResolveBlobUpload(string path, string method)
    {
        var idx = path.IndexOf("/blobs/uploads", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var remainder = path[(idx + 14)..].Trim('/');

        return HttpMethods.IsPost(method)
            ? new(OciOperationKind.StartBlobUpload, Repo: repo)
            : string.IsNullOrEmpty(remainder)
                ? new(OciOperationKind.Unknown)
                : method switch
                {
                    _ when HttpMethods.IsPatch(method) => new(OciOperationKind.AppendBlobUploadChunk, Repo: repo, UploadId: remainder),
                    _ when HttpMethods.IsPut(method) => new(OciOperationKind.CommitBlobUpload, Repo: repo, UploadId: remainder),
                    _ when HttpMethods.IsGet(method) => new(OciOperationKind.GetBlobUploadStatus, Repo: repo, UploadId: remainder),
                    _ when HttpMethods.IsDelete(method) => new(OciOperationKind.CancelBlobUpload, Repo: repo, UploadId: remainder),
                    _ => new(OciOperationKind.Unknown)
                };
    }

    private static OciRequestTarget ResolveBlob(string path, string method)
    {
        var idx = path.IndexOf("/blobs/", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var digest = path[(idx + 7)..].Trim();

        return method switch
        {
            _ when HttpMethods.IsHead(method) => new(OciOperationKind.HeadBlob, Repo: repo, Reference: digest),
            _ when HttpMethods.IsGet(method) => new(OciOperationKind.GetBlob, Repo: repo, Reference: digest),
            _ => new(OciOperationKind.Unknown)
        };
    }

    private static OciRequestTarget ResolveManifest(string path, string method)
    {
        var idx = path.IndexOf("/manifests/", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var reference = path[(idx + 11)..].Trim();

        return method switch
        {
            _ when HttpMethods.IsHead(method) => new(OciOperationKind.HeadManifest, Repo: repo, Reference: reference),
            _ when HttpMethods.IsGet(method) => new(OciOperationKind.GetManifest, Repo: repo, Reference: reference),
            _ when HttpMethods.IsPut(method) => new(OciOperationKind.PutManifest, Repo: repo, Reference: reference),
            _ when HttpMethods.IsDelete(method) => new(OciOperationKind.DeleteManifest, Repo: repo, Reference: reference),
            _ => new(OciOperationKind.Unknown)
        };
    }
}
