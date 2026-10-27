using System.Text.Json;
using Vessel3.IO;

namespace Vessel3.Server.Endpoints;

internal static class ClusterEndpoints
{
    private const string ActionClusterGossip = "ClusterGossip";
    private const string ActionClusterHeadBlob = "ClusterHeadBlob";
    private const string ActionClusterGetBlob = "ClusterGetBlob";

    public static void MapClusterEndpoints(this WebApplication app)
    {
        var cluster = app.MapGroup(ReservedRoutePrefixes.Cluster);
        cluster.MapPost(VesselRoutes.ClusterGossipSubpath, HandleGossip);
        cluster.MapMethods(VesselRoutes.ClusterBlobRoutePattern, [HttpMethods.Head], HandleHeadBlob);
        cluster.MapGet(VesselRoutes.ClusterBlobRoutePattern, HandleGetBlob);
    }

    private static async Task HandleGossip(
        HttpContext ctx,
        ClusterMembershipOptions options,
        IClusterMembership membership)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetAction(ActionClusterGossip);

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

        RequestTrace.SetContext(actor: incoming.SenderNodeId);
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
        IBlobPool blobPool)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterHeadBlob, key: sha);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!Sha256Hex.IsValid(sha))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.StatusCode = await blobPool.ExistsOnLocalVolume(sha, ctx.RequestAborted)
            ? StatusCodes.Status200OK
            : StatusCodes.Status404NotFound;
    }

    private static async Task HandleGetBlob(
        string sha,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IBlobPool blobPool)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterGetBlob, key: sha);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!Sha256Hex.IsValid(sha))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var openResult = await blobPool.OpenLocalVolumeOnly(sha, ctx.RequestAborted);
        if (!openResult.TryGetValue(out var stream, out _))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (stream)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = VesselMediaTypes.OctetStream;
            if (stream.CanSeek)
                ctx.Response.ContentLength = stream.Length;

            await stream.CopyToAsync(ctx.Response.Body, SohBuffer.BlockSize, ctx.RequestAborted);
        }
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
