using System.Text.Json;

namespace Vessel3.Server.Endpoints;

internal static class ClusterEndpoints
{
    public static void MapClusterEndpoints(this WebApplication app)
    {
        var cluster = app.MapGroup(ReservedRoutePrefixes.Cluster);
        cluster.MapPost("/gossip", HandleGossip);
        cluster.MapMethods("/blobs/{sha}", [HttpMethods.Head], HandleHeadBlob);
        cluster.MapGet("/blobs/{sha}", HandleGetBlob);
    }

    private static async Task HandleGossip(
        HttpContext ctx,
        ClusterMembershipOptions options,
        IClusterMembership membership)
    {
        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        GossipMessage? incoming;
        try
        {
            incoming = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                GossipJsonContext.Default.GossipMessage,
                ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (incoming is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var reply = membership.MergeGossip(incoming);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            reply,
            GossipJsonContext.Default.GossipMessage,
            ctx.RequestAborted);
    }

    private static async Task HandleHeadBlob(
        string sha,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IVolumeRegistry volumeRegistry)
    {
        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!HttpRemoteBlobReader.IsValidSha(sha))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        foreach (var storage in volumeRegistry.LocalStorages)
        {
            if (await storage.BlobExists(sha, ctx.RequestAborted))
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task HandleGetBlob(
        string sha,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IVolumeRegistry volumeRegistry)
    {
        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!HttpRemoteBlobReader.IsValidSha(sha))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        foreach (var storage in volumeRegistry.LocalStorages)
        {
            var openResult = await storage.OpenBlob(sha, ctx.RequestAborted);
            if (!openResult.TryGetValue(out var stream, out _))
                continue;

            await using (stream)
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = VesselMediaTypes.OctetStream;
                if (stream.CanSeek)
                    ctx.Response.ContentLength = stream.Length;

                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsAuthorized(HttpContext ctx, ClusterMembershipOptions options)
    {
        if (string.IsNullOrEmpty(options.ClusterSecret))
            return true;

        if (!ctx.Request.Headers.TryGetValue(VesselHeaders.ClusterSecret, out var provided) || provided.Count == 0)
            return false;

        return CryptographicCompare.FixedTimeEquals(provided[0].AsSpan(), options.ClusterSecret.AsSpan());
    }
}
