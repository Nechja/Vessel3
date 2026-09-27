using System.Globalization;
using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Storage;
using Vessel3.Storage.Lifecycle;

namespace Vessel3.Server.Hosting;

internal sealed class NativeProtocol : IVesselProtocol
{
    public string Name => "Native";

    public void ConfigureServices(IServiceCollection services, VesselConfig config)
    {
        var isUnauthenticated = config.AccessKey is null && config.SecretKey is null && config.Oidc is null;
        var options = new NativeAuthOptions(isUnauthenticated, config.AccessKey, config.SecretKey);
        services.AddSingleton(options);

        if (config.Oidc is not null)
        {
            services.AddSingleton<ITokenAuthenticator, OidcTokenAuthenticator>();
        }

        services.AddSingleton<NativeAuthMiddleware>();
    }

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config)
    {
        app.UseMiddleware<NativeAuthMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/iam/whoami", static (HttpContext ctx) =>
        {
            var caller = ctx.GetCaller();
            return caller is null
                ? ToHttpResult(new AccessDeniedError("Unauthorized"))
                : Results.Json(
                    new WhoAmIDto(caller.UserId, caller.Username, caller.Role.ToString(), caller.AccessKeyId),
                    NativeJsonContext.Default.WhoAmIDto);
        });

        endpoints.MapGet("/v1/buckets", static (HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var buckets = caller is not null ? registry.List(caller) : registry.List();
            List<BucketDto> dtos = [.. buckets.Select(b => new BucketDto(b.Name, b.CreatedAt, b.OwnerId))];
            return Results.Json(dtos, NativeJsonContext.Default.ListBucketDto);
        });

        endpoints.MapPut("/v1/buckets/{bucket}", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var result = caller is not null ? registry.Create(bucket, caller) : registry.Create(bucket);
            return result.Match(_ => Results.Ok(), ToHttpResult);
        });

        endpoints.MapDelete("/v1/buckets/{bucket}", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var result = caller is not null ? registry.Delete(bucket, caller) : registry.Delete(bucket);
            return result.Match(() => Results.NoContent(), ToHttpResult);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var result = registry.GetAccess(bucket);
            return result.Match(
                access => Results.Json(new BucketAccessDto(access.PublicRead, access.ReadOnly), NativeJsonContext.Default.BucketAccessDto),
                ToHttpResult);
        });

        endpoints.MapPut("/v1/buckets/{bucket}/access", static async (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketAccessDto);
            var result = registry.SetAccess(bucket, new BucketAccess(body.PublicRead, body.ReadOnly));
            return result.Match(() => Results.Ok(), ToHttpResult);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/versioning", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var result = registry.GetVersioning(bucket);
            return result.Match(
                status => Results.Json(new BucketVersioningDto(status.ToString()), NativeJsonContext.Default.BucketVersioningDto),
                ToHttpResult);
        });

        endpoints.MapPut("/v1/buckets/{bucket}/versioning", static async (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketVersioningDto);
            if (!Enum.TryParse<VersioningStatus>(body.Status, ignoreCase: true, out var status))
                return ToHttpResult(new InvalidArgumentError("Invalid versioning status"));

            var result = registry.SetVersioning(bucket, status);
            return result.Match(() => Results.Ok(), ToHttpResult);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/objects", static (
            string bucket,
            HttpContext ctx,
            IBucketLister lister,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var prefix = Nullify(ctx.Request.Query["prefix"].ToString());
            var marker = Nullify(ctx.Request.Query["marker"].ToString());
            var delimiter = Nullify(ctx.Request.Query["delimiter"].ToString());
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) && l > 0 ? l : 1000;

            var result = lister.List(new ListRequest(bucket, prefix, delimiter, marker, limit), continuationToken: null);
            if (!result.TryGetValue(out var page, out var err))
                return ToHttpResult(err);

            List<ObjectSummaryDto> objectsList = [.. page.Entries.OfType<ListEntry.Contents>()
                .Select(c => new ObjectSummaryDto(c.Key, c.Size, c.Etag, c.LastModified, null))];
            List<string> prefixes = [.. page.Entries.OfType<ListEntry.CommonPrefix>()
                .Select(p => p.Key)];
            var nextMarker = page.NextContinuationToken ?? page.LastKey;

            var dto = new ObjectsPageDto(objectsList, prefixes, page.IsTruncated, nextMarker);
            return Results.Json(dto, NativeJsonContext.Default.ObjectsPageDto);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/objects/{**key}", static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var versionId = Nullify(ctx.Request.Query["versionId"].ToString());
            var result = objects.Get(bucket, key, versionId);
            if (!result.TryGetValue(out var obj, out var err))
                return ToHttpResult(err);

            ctx.Response.Headers.ETag = $"\"{obj.Etag}\"";
            if (obj.ContentType is { Length: > 0 } ct)
                ctx.Response.Headers.ContentType = ct;
            ctx.Response.Headers.ContentLength = obj.Size;
            ctx.Response.Headers.LastModified = obj.LastModified.ToString("R", CultureInfo.InvariantCulture);

            if (versionId is not null)
                ctx.Response.Headers["X-Vessel-Version-Id"] = versionId;
            else if (registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) && cur?.VersionId is { } curVer)
                ctx.Response.Headers["X-Vessel-Version-Id"] = curVer;

            if (obj.Metadata is not null)
            {
                foreach (var (k, v) in obj.Metadata)
                {
                    ctx.Response.Headers[$"X-Vessel-Meta-{k}"] = v;
                }
            }

            return Results.Stream(obj.Body, obj.ContentType ?? "application/octet-stream");
        });

        endpoints.MapMethods("/v1/buckets/{bucket}/objects/{**key}", ["HEAD"], static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var versionId = Nullify(ctx.Request.Query["versionId"].ToString());
            var result = objects.Stat(bucket, key, versionId);
            if (!result.TryGetValue(out var stat, out var err))
                return ToHttpResult(err);

            ctx.Response.Headers.ETag = $"\"{stat.Etag}\"";
            if (stat.ContentType is { Length: > 0 } ct)
                ctx.Response.Headers.ContentType = ct;
            ctx.Response.Headers.ContentLength = stat.Size;
            ctx.Response.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);

            if (versionId is not null)
                ctx.Response.Headers["X-Vessel-Version-Id"] = versionId;
            else if (registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) && cur?.VersionId is { } curVer)
                ctx.Response.Headers["X-Vessel-Version-Id"] = curVer;

            if (stat.Metadata is not null)
            {
                foreach (var (k, v) in stat.Metadata)
                {
                    ctx.Response.Headers[$"X-Vessel-Meta-{k}"] = v;
                }
            }

            return Results.Ok();
        });

        endpoints.MapPut("/v1/buckets/{bucket}/objects/{**key}", static async (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in ctx.Request.Headers)
            {
                if (header.Key.StartsWith("X-Vessel-Meta-", StringComparison.OrdinalIgnoreCase))
                {
                    var metaKey = header.Key["X-Vessel-Meta-".Length..];
                    metadata[metaKey] = header.Value.ToString();
                }
            }

            var result = await objects.Put(
                bucket,
                key,
                ctx.Request.Body,
                ctx.Request.ContentLength,
                ctx.Request.ContentType,
                declaredSha256: null,
                declaredMd5Base64: null,
                metadata,
                tags: new Dictionary<string, string>(),
                declaredChecksums: ChecksumSet.Empty,
                ctx.RequestAborted);

            return result.Match(
                res => Results.Json(new PutObjectResultDto(res.Etag, res.VersionId, res.Size, res.Sha256), NativeJsonContext.Default.PutObjectResultDto),
                ToHttpResult);
        });

        endpoints.MapDelete("/v1/buckets/{bucket}/objects/{**key}", static (
            string bucket,
            string key,
            HttpContext ctx,
            IObjectStore objects,
            IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
                return ToHttpResult(authFail.Error);

            var versionId = Nullify(ctx.Request.Query["versionId"].ToString());
            var result = versionId is not null
                ? objects.DeleteVersion(bucket, key, versionId)
                : objects.Delete(bucket, key);

            return result.Match(_ => Results.NoContent(), ToHttpResult);
        });

        endpoints.MapGet("/v1/iam/users", static (HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
                return ToHttpResult(new AccessDeniedError("Admin role required"));

            var result = identity.ListUsers();
            return result.Match(
                users => Results.Json<List<UserDto>>([.. users.Select(u => new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt))], NativeJsonContext.Default.ListUserDto),
                ToHttpResult);
        });

        endpoints.MapPost("/v1/iam/users", static async (HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
                return ToHttpResult(new AccessDeniedError("Admin role required"));

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateUserRequest);
            if (string.IsNullOrWhiteSpace(body.Username))
                return ToHttpResult(new InvalidArgumentError("Username is required"));

            var role = Enum.TryParse<UserRole>(body.Role, ignoreCase: true, out var r) ? r : UserRole.Member;
            var result = identity.CreateUser(body.Username, role);
            return result.Match(
                u => Results.Json(new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt), NativeJsonContext.Default.UserDto, statusCode: 201),
                ToHttpResult);
        });

        endpoints.MapGet("/v1/iam/users/{userId}", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
                return ToHttpResult(new AccessDeniedError("Forbidden"));

            var result = identity.GetUser(userId);
            return result.Match(
                u => u is not null
                    ? Results.Json(new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt), NativeJsonContext.Default.UserDto)
                    : ToHttpResult(new NotFoundError($"User {userId}")),
                ToHttpResult);
        });

        endpoints.MapDelete("/v1/iam/users/{userId}", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
                return ToHttpResult(new AccessDeniedError("Admin role required"));

            var result = identity.DeleteUser(userId);
            return result.Match(() => Results.NoContent(), ToHttpResult);
        });

        endpoints.MapGet("/v1/iam/users/{userId}/keys", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
                return ToHttpResult(new AccessDeniedError("Forbidden"));

            var result = identity.ListAccessKeys(userId);
            return result.Match(
                keys => Results.Json<List<AccessKeyDto>>([.. keys.Select(k => new AccessKeyDto(k.Id, k.SecretKey, k.UserId, k.Description, k.CreatedAt, k.ExpiresAt, k.IsRevoked))], NativeJsonContext.Default.ListAccessKeyDto),
                ToHttpResult);
        });

        endpoints.MapPost("/v1/iam/users/{userId}/keys", static async (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
                return ToHttpResult(new AccessDeniedError("Forbidden"));

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateAccessKeyRequest);
            var ttl = body.TtlSeconds.HasValue ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : (TimeSpan?)null;
            var result = identity.CreateAccessKey(userId, body.Description, ttl);
            return result.Match(
                k => Results.Json(new AccessKeyDto(k.Id, k.SecretKey, k.UserId, k.Description, k.CreatedAt, k.ExpiresAt, k.IsRevoked), NativeJsonContext.Default.AccessKeyDto, statusCode: 201),
                ToHttpResult);
        });

        endpoints.MapDelete("/v1/iam/keys/{accessKeyId}", static (string accessKeyId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null)
                return ToHttpResult(new AccessDeniedError("Unauthorized"));

            var keyResult = identity.GetAccessKey(accessKeyId);
            if (!keyResult.TryGetValue(out var key, out var err))
                return ToHttpResult(err);

            if (key is null)
                return ToHttpResult(new NotFoundError($"AccessKey {accessKeyId}"));

            if (!caller.IsAdmin && key.UserId != caller.UserId)
                return ToHttpResult(new AccessDeniedError("Forbidden"));

            var result = identity.RevokeAccessKey(accessKeyId);
            return result.Match(() => Results.NoContent(), ToHttpResult);
        });

        endpoints.MapPost("/v1/admin/gc", static async (HttpContext ctx, IGarbageCollector gc) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
                return ToHttpResult(new AccessDeniedError("Admin role required"));

            var blobAge = long.TryParse(ctx.Request.Query["minBlobAgeSec"], out var b) ? b : 3600;
            var uploadAge = long.TryParse(ctx.Request.Query["minUploadAgeSec"], out var u) ? u : 604800;
            var report = await gc.Run(TimeSpan.FromSeconds(blobAge), TimeSpan.FromSeconds(uploadAge));
            return Results.Json(new GcReportDto(report.BlobsDeleted, report.UploadsReaped), NativeJsonContext.Default.GcReportDto);
        });

        endpoints.MapPost("/v1/admin/sweep", static (HttpContext ctx, ILifecycleSweeper sweeper) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
                return ToHttpResult(new AccessDeniedError("Admin role required"));

            var now = DateTimeOffset.UtcNow;
            if (ctx.Request.Query.TryGetValue("now", out var nowRaw) &&
                DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                now = parsed;
            }

            var report = sweeper.Run(now);
            return Results.Json(new SweepReportDto(report.Expired, report.MarkersReaped), NativeJsonContext.Default.SweepReportDto);
        });
    }

    private static IResult ToHttpResult(Error error) =>
        Results.Json(new ErrorDto(error.Code, error.Message), NativeJsonContext.Default.ErrorDto, statusCode: error.Status);

    private static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
