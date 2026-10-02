using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci;

internal interface IOciDispatcher
{
    Task DispatchAsync(string path, HttpContext ctx);
}

internal sealed class OciDispatcher(
    IContainerRepoCatalog catalog,
    IBlobPool blobs,
    IIdentityRegistry identity,
    IContainerRepoTokenService tokenService,
    ContainerRepoAuthOptions options) : IOciDispatcher
{
    public async Task DispatchAsync(string path, HttpContext ctx)
    {
        var cleanPath = path.Trim('/');
        var method = ctx.Request.Method;

        if (cleanPath == "" || cleanPath.Equals("v2", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePing(ctx);
            return;
        }

        if (cleanPath.Equals("token", StringComparison.OrdinalIgnoreCase))
        {
            await HandleToken(ctx);
            return;
        }

        if (cleanPath.Equals("_catalog", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCatalog(ctx);
            return;
        }

        if (cleanPath.Contains("tags/list", StringComparison.OrdinalIgnoreCase))
        {
            var repo = cleanPath[..cleanPath.IndexOf("/tags/list", StringComparison.OrdinalIgnoreCase)];
            await HandleTags(repo, ctx);
            return;
        }

        if (cleanPath.Contains("blobs/uploads", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBlobUploads(cleanPath, method, ctx);
            return;
        }

        if (cleanPath.Contains("/blobs/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBlobs(cleanPath, method, ctx);
            return;
        }

        if (cleanPath.Contains("/manifests/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleManifests(cleanPath, method, ctx);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static Task HandlePing(HttpContext ctx)
    {
        ctx.Response.Headers.Append("Docker-Distribution-API-Version", "registry/2.0");
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync("{}", ctx.RequestAborted);
    }

    private async Task HandleToken(HttpContext ctx)
    {
        var scope = ctx.Request.Query["scope"].ToString();

        string? userId = null;
        var canWrite = true;

        var authHeader = ctx.Request.Headers.Authorization.ToString().Trim();
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var basicStr = authHeader["Basic ".Length..].Trim();
            if (TryDecodeBasic(basicStr, out var key, out var secret))
            {
                if (options.RootAccessKey is not null && options.RootSecretKey is not null &&
                    string.Equals(key, options.RootAccessKey, StringComparison.Ordinal))
                {
                    if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(options.RootSecretKey), Encoding.UTF8.GetBytes(secret)))
                    {
                        userId = "admin";
                        canWrite = true;
                    }
                }
                else if (identity.GetAccessKey(key).TryGetValue(out var keyEntry, out _) && keyEntry is not null)
                {
                    var keySecretBytes = Encoding.UTF8.GetBytes(keyEntry.SecretKey);
                    var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
                    if (CryptographicOperations.FixedTimeEquals(keySecretBytes, reqSecretBytes))
                    {
                        if (identity.AuthenticateAccessKey(key).TryGetValue(out var caller, out _) && caller is not null)
                        {
                            userId = caller.UserId;
                            canWrite = caller.CanWrite;
                        }
                    }
                }
            }
        }
        else if (options.IsUnauthenticated)
        {
            userId = "anonymous";
            canWrite = true;
        }

        if (userId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var grantedScopes = new List<string>();
        if (!string.IsNullOrEmpty(scope))
        {
            var parts = scope.Split(':');
            if (parts.Length >= 3 && parts[0] == "repository")
            {
                var repoName = parts[1];
                var requestedActions = parts[2].Split(',');
                var allowedActions = requestedActions
                    .Where(a => a == "pull" || (a == "push" && canWrite) || (a == "*" && canWrite))
                    .ToList();

                if (allowedActions.Count > 0)
                {
                    grantedScopes.Add($"repository:{repoName}:{string.Join(",", allowedActions)}");
                }
            }
        }

        var ttl = TimeSpan.FromHours(1);
        var token = tokenService.CreateToken(userId, grantedScopes, ttl);
        var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var responseDto = new TokenResponseDto(
            Token: token,
            AccessToken: token,
            ExpiresIn: (int)ttl.TotalSeconds,
            IssuedAt: nowIso);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, responseDto, OciJsonContext.Default.TokenResponseDto, ctx.RequestAborted);
    }

    private async Task HandleCatalog(HttpContext ctx)
    {
        var nStr = ctx.Request.Query["n"].ToString();
        var limit = int.TryParse(nStr, out var parsedLimit) ? parsedLimit : 100;
        var last = ctx.Request.Query["last"].ToString();
        if (string.IsNullOrEmpty(last)) last = null;

        var listResult = catalog.ListRepos(limit, last);
        if (!listResult.TryGetValue(out var repos, out var err))
        {
            await WriteOciError(ctx, StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", err.Message);
            return;
        }

        if (repos.Count == limit && repos.Count > 0)
        {
            var nextLast = repos[^1];
            ctx.Response.Headers.Append("Link", $"</v2/_catalog?n={limit}&last={Uri.EscapeDataString(nextLast)}>; rel=\"next\"");
        }

        var dto = new CatalogListDto(repos);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, dto, OciJsonContext.Default.CatalogListDto, ctx.RequestAborted);
    }

    private async Task HandleTags(string repo, HttpContext ctx)
    {
        var nStr = ctx.Request.Query["n"].ToString();
        var limit = int.TryParse(nStr, out var parsedLimit) ? parsedLimit : 100;
        var last = ctx.Request.Query["last"].ToString();
        if (string.IsNullOrEmpty(last)) last = null;

        var tagsResult = catalog.ListTags(repo, limit, last);
        if (!tagsResult.TryGetValue(out var tags, out var err))
        {
            var code = err is NoSuchContainerRepoError ? "NAME_UNKNOWN" : "INTERNAL_ERROR";
            var status = err is NoSuchContainerRepoError ? StatusCodes.Status404NotFound : StatusCodes.Status500InternalServerError;
            await WriteOciError(ctx, status, code, err.Message);
            return;
        }

        if (tags.Count == limit && tags.Count > 0)
        {
            var nextLast = tags[^1];
            ctx.Response.Headers.Append("Link", $"</v2/{repo}/tags/list?n={limit}&last={Uri.EscapeDataString(nextLast)}>; rel=\"next\"");
        }

        var dto = new TagsListDto(repo, tags);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, dto, OciJsonContext.Default.TagsListDto, ctx.RequestAborted);
    }

    private async Task HandleBlobs(string path, string method, HttpContext ctx)
    {
        var idx = path.IndexOf("/blobs/", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var digest = path[(idx + 7)..].Trim();
        var cleanSha = CleanSha(digest);

        if (HttpMethods.IsHead(method))
        {
            if (!blobs.Exists(cleanSha))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UNKNOWN", $"Blob unknown: {digest}");
                return;
            }

            var openResult = blobs.Open(cleanSha);
            if (!openResult.TryGetValue(out var stream, out _))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UNKNOWN", $"Blob unknown: {digest}");
                return;
            }

            using (stream)
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers.Append("Docker-Content-Digest", $"sha256:{cleanSha}");
                ctx.Response.ContentLength = stream.Length;
            }
            return;
        }

        if (HttpMethods.IsGet(method))
        {
            if (!blobs.Exists(cleanSha))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UNKNOWN", $"Blob unknown: {digest}");
                return;
            }

            var openResult = blobs.Open(cleanSha);
            if (!openResult.TryGetValue(out var stream, out _))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UNKNOWN", $"Blob unknown: {digest}");
                return;
            }

            await using (stream)
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers.Append("Docker-Content-Digest", $"sha256:{cleanSha}");
                ctx.Response.ContentLength = stream.Length;
                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }

    private async Task HandleBlobUploads(string path, string method, HttpContext ctx)
    {
        var idx = path.IndexOf("/blobs/uploads", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var remainder = path[(idx + 14)..].Trim('/');

        if (HttpMethods.IsPost(method))
        {
            var declaredDigest = ctx.Request.Query["digest"].ToString();
            if (!string.IsNullOrEmpty(declaredDigest))
            {
                var cleanDeclaredSha = CleanSha(declaredDigest);
                var writeResult = await blobs.Write(ctx.Request.Body, ctx.Request.ContentLength, ChecksumIntent.None, ctx.RequestAborted);
                if (!writeResult.TryGetValue(out var storedBlob, out var writeErr))
                {
                    await WriteOciError(ctx, StatusCodes.Status400BadRequest, "BLOB_UPLOAD_INVALID", writeErr.Message);
                    return;
                }

                if (!string.Equals(storedBlob.Sha, cleanDeclaredSha, StringComparison.OrdinalIgnoreCase))
                {
                    blobs.Delete(storedBlob.Sha);
                    await WriteOciError(ctx, StatusCodes.Status400BadRequest, "DIGEST_INVALID", $"Declared digest {declaredDigest} did not match actual sha256:{storedBlob.Sha}");
                    return;
                }

                catalog.GetOrCreateRepo(repo);
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.Headers.Append("Location", $"/v2/{repo}/blobs/sha256:{storedBlob.Sha}");
                ctx.Response.Headers.Append("Docker-Content-Digest", $"sha256:{storedBlob.Sha}");
                return;
            }

            var sessionResult = catalog.StartUploadSession(repo);
            if (!sessionResult.TryGetValue(out var session, out var sessionErr))
            {
                await WriteOciError(ctx, StatusCodes.Status400BadRequest, "BLOB_UPLOAD_INVALID", sessionErr.Message);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            ctx.Response.Headers.Append("Location", $"/v2/{repo}/blobs/uploads/{session.Id}");
            ctx.Response.Headers.Append("Range", "bytes=0-0");
            ctx.Response.Headers.Append("Docker-Upload-UUID", session.Id);
            return;
        }

        var uploadId = remainder;
        if (string.IsNullOrEmpty(uploadId))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (HttpMethods.IsPatch(method))
        {
            var sessionResult = catalog.GetUploadSession(uploadId);
            if (!sessionResult.TryGetValue(out var session, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UPLOAD_UNKNOWN", err.Message);
                return;
            }

            await using (var fileStream = new FileStream(session.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await ctx.Request.Body.CopyToAsync(fileStream, ctx.RequestAborted);
            }

            var fileInfo = new FileInfo(session.TempFilePath);
            var totalBytes = fileInfo.Length;
            catalog.UpdateUploadSession(uploadId, totalBytes);

            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            ctx.Response.Headers.Append("Location", $"/v2/{repo}/blobs/uploads/{uploadId}");
            ctx.Response.Headers.Append("Range", $"bytes=0-{Math.Max(0, totalBytes - 1)}");
            ctx.Response.Headers.Append("Docker-Upload-UUID", uploadId);
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            var declaredDigest = ctx.Request.Query["digest"].ToString();
            if (string.IsNullOrEmpty(declaredDigest))
            {
                await WriteOciError(ctx, StatusCodes.Status400BadRequest, "DIGEST_INVALID", "Missing digest query parameter on upload commit");
                return;
            }

            var sessionResult = catalog.GetUploadSession(uploadId);
            if (!sessionResult.TryGetValue(out var session, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UPLOAD_UNKNOWN", err.Message);
                return;
            }

            if (ctx.Request.ContentLength is > 0)
            {
                await using var fileStream = new FileStream(session.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await ctx.Request.Body.CopyToAsync(fileStream, ctx.RequestAborted);
            }

            var cleanDeclaredSha = CleanSha(declaredDigest);

            StoredBlob storedBlob;
            await using (var readFileStream = new FileStream(session.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var writeResult = await blobs.Write(readFileStream, readFileStream.Length, ChecksumIntent.None, ctx.RequestAborted);
                if (!writeResult.TryGetValue(out var b, out var writeErr))
                {
                    await WriteOciError(ctx, StatusCodes.Status400BadRequest, "BLOB_UPLOAD_INVALID", writeErr.Message);
                    return;
                }
                storedBlob = b;
            }

            if (!string.Equals(storedBlob.Sha, cleanDeclaredSha, StringComparison.OrdinalIgnoreCase))
            {
                blobs.Delete(storedBlob.Sha);
                catalog.CancelUploadSession(uploadId);
                await WriteOciError(ctx, StatusCodes.Status400BadRequest, "DIGEST_INVALID", $"Declared digest {declaredDigest} does not match computed sha256:{storedBlob.Sha}");
                return;
            }

            catalog.CompleteUploadSession(uploadId);
            try { File.Delete(session.TempFilePath); } catch { }

            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.Headers.Append("Location", $"/v2/{repo}/blobs/sha256:{storedBlob.Sha}");
            ctx.Response.Headers.Append("Docker-Content-Digest", $"sha256:{storedBlob.Sha}");
            return;
        }

        if (HttpMethods.IsGet(method))
        {
            var sessionResult = catalog.GetUploadSession(uploadId);
            if (!sessionResult.TryGetValue(out var session, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "BLOB_UPLOAD_UNKNOWN", err.Message);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            ctx.Response.Headers.Append("Range", $"bytes=0-{Math.Max(0, session.BytesReceived - 1)}");
            ctx.Response.Headers.Append("Docker-Upload-UUID", uploadId);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            catalog.CancelUploadSession(uploadId);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }

    private async Task HandleManifests(string path, string method, HttpContext ctx)
    {
        var idx = path.IndexOf("/manifests/", StringComparison.OrdinalIgnoreCase);
        var repo = path[..idx].Trim('/');
        var reference = path[(idx + 11)..].Trim();

        if (HttpMethods.IsHead(method))
        {
            var manifestResult = catalog.GetManifest(repo, reference);
            if (!manifestResult.TryGetValue(out var manifest, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "MANIFEST_UNKNOWN", err.Message);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = manifest.MediaType;
            ctx.Response.Headers.Append("Docker-Content-Digest", manifest.Digest);
            ctx.Response.ContentLength = manifest.Size;
            return;
        }

        if (HttpMethods.IsGet(method))
        {
            var manifestResult = catalog.GetManifest(repo, reference);
            if (!manifestResult.TryGetValue(out var manifest, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "MANIFEST_UNKNOWN", err.Message);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = manifest.MediaType;
            ctx.Response.Headers.Append("Docker-Content-Digest", manifest.Digest);
            ctx.Response.ContentLength = manifest.Size;
            await ctx.Response.Body.WriteAsync(manifest.Content, ctx.RequestAborted);
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var payload = ms.ToArray();

            if (payload.Length == 0)
            {
                await WriteOciError(ctx, StatusCodes.Status400BadRequest, "MANIFEST_INVALID", "Manifest payload cannot be empty");
                return;
            }

            var mediaType = ctx.Request.ContentType ?? "application/vnd.docker.distribution.manifest.v2+json";
            var layerDigests = ExtractReferencedDigests(payload);

            var putResult = catalog.PutManifest(repo, reference, mediaType, payload, layerDigests);
            if (!putResult.TryGetValue(out var outcome, out var putErr))
            {
                await WriteOciError(ctx, StatusCodes.Status400BadRequest, "MANIFEST_INVALID", putErr.Message);
                return;
            }

            await blobs.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.None, ctx.RequestAborted);

            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.Headers.Append("Location", $"/v2/{repo}/manifests/{outcome.Digest}");
            ctx.Response.Headers.Append("Docker-Content-Digest", outcome.Digest);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            var delResult = catalog.DeleteManifest(repo, reference);
            if (!delResult.TryGetValue(out var deleted, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status404NotFound, "MANIFEST_UNKNOWN", err.Message);
                return;
            }

            ctx.Response.StatusCode = deleted ? StatusCodes.Status202Accepted : StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }

    private static string CleanSha(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].ToLowerInvariant()
            : digest.ToLowerInvariant();

    private static bool TryDecodeBasic(string basicStr, out string key, out string secret)
    {
        key = "";
        secret = "";
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(basicStr));
            var idx = decoded.IndexOf(':');
            if (idx <= 0) return false;
            key = decoded[..idx];
            secret = decoded[(idx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractReferencedDigests(byte[] payload)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var cfgDigest))
            {
                if (cfgDigest.GetString() is { } cd) list.Add(cd);
            }

            if (root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            {
                foreach (var layer in layers.EnumerateArray())
                {
                    if (layer.TryGetProperty("digest", out var lDigest) && lDigest.GetString() is { } ld)
                    {
                        list.Add(ld);
                    }
                }
            }

            if (root.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in manifests.EnumerateArray())
                {
                    if (m.TryGetProperty("digest", out var mDigest) && mDigest.GetString() is { } md)
                    {
                        list.Add(md);
                    }
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private static async Task WriteOciError(HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        var errDto = new OciErrorResponseDto([new OciErrorItemDto(code, message, null)]);
        await JsonSerializer.SerializeAsync(ctx.Response.Body, errDto, OciJsonContext.Default.OciErrorResponseDto);
    }
}
