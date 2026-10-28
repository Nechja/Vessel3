using System.Globalization;
using System.Text.Json;
using Vessel3.IO;

namespace Vessel3.Server.Endpoints;

internal static class ClusterEndpoints
{
    private const string ActionClusterGossip = "ClusterGossip";
    private const string ActionClusterHeadBlob = "ClusterHeadBlob";
    private const string ActionClusterGetBlob = "ClusterGetBlob";
    private const string ActionClusterGetCatalog = "ClusterGetCatalog";
    private const string ActionClusterGetBucketEvents = "ClusterGetBucketEvents";
    private const string ActionClusterGetBucketSnapshot = "ClusterGetBucketSnapshot";
    private const string ActionClusterGetIamDelta = "ClusterGetIamDelta";
    private const string ActionClusterGetUploadSession = "ClusterGetUploadSession";
    private const string ActionClusterDeleteUploadSession = "ClusterDeleteUploadSession";
    private const string ActionClusterGetGcReferences = "ClusterGetGcReferences";

    public static void MapClusterEndpoints(this WebApplication app)
    {
        var cluster = app.MapGroup(ReservedRoutePrefixes.Cluster);
        cluster.MapPost(VesselRoutes.ClusterGossipSubpath, HandleGossip);
        cluster.MapMethods(VesselRoutes.ClusterBlobRoutePattern, [HttpMethods.Head], HandleHeadBlob);
        cluster.MapGet(VesselRoutes.ClusterBlobRoutePattern, HandleGetBlob);
        cluster.MapGet(VesselRoutes.ClusterCatalogSubpath, HandleGetCatalog);
        cluster.MapGet(VesselRoutes.ClusterBucketEventsRoutePattern, HandleGetBucketEvents);
        cluster.MapGet(VesselRoutes.ClusterBucketSnapshotRoutePattern, HandleGetBucketSnapshot);
        cluster.MapGet(VesselRoutes.ClusterIamDeltaSubpath, HandleGetIamDelta);
        cluster.MapGet(VesselRoutes.ClusterUploadsRoutePattern, HandleGetUploadSession);
        cluster.MapDelete(VesselRoutes.ClusterUploadsRoutePattern, HandleDeleteUploadSession);
        cluster.MapGet(VesselRoutes.ClusterGcReferencesSubpath, HandleGetGcReferences);
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

    private static async Task HandleGetCatalog(
        HttpContext ctx,
        ClusterMembershipOptions options,
        IBucketRegistry bucketRegistry,
        IIdentityRegistry identityRegistry)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetAction(ActionClusterGetCatalog);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var snapshot = new ClusterCatalogSnapshot(
            options.NodeId,
            identityRegistry.Generation,
            bucketRegistry.GetCatalogEntries());

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            snapshot,
            GossipJsonContext.Default.ClusterCatalogSnapshot,
            ctx.RequestAborted);
    }

    private static async Task HandleGetBucketEvents(
        string bucket,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IBucketRegistry bucketRegistry)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterGetBucketEvents, bucket: bucket);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var targetBucket = bucketRegistry.GetOpenBucket(bucket);
        if (targetBucket is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var afterSeq = 0L;
        if (ctx.Request.Query.TryGetValue(VesselQueryParams.AfterSeq, out var rawAfter)
            && long.TryParse(rawAfter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            afterSeq = Math.Max(0L, parsed);
        }

        var firstAvail = targetBucket.FirstAvailableLogSeq;
        var appliedSeq = targetBucket.AppliedSeq;
        if (afterSeq < firstAvail - 1)
        {
            var requiresSnapBatch = new BucketEventsBatch(
                bucket,
                firstAvail,
                appliedSeq,
                RequiresSnapshot: true,
                Events: []);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = VesselMediaTypes.Json;
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                requiresSnapBatch,
                GossipJsonContext.Default.BucketEventsBatch,
                ctx.RequestAborted);
            return;
        }

        var events = targetBucket.ReadEventsAfter(afterSeq, 1000);
        var batch = new BucketEventsBatch(
            bucket,
            firstAvail,
            appliedSeq,
            RequiresSnapshot: false,
            Events: events);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            batch,
            GossipJsonContext.Default.BucketEventsBatch,
            ctx.RequestAborted);
    }

    private static async Task HandleGetBucketSnapshot(
        string bucket,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IBucketRegistry bucketRegistry)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterGetBucketSnapshot, bucket: bucket);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var targetBucket = bucketRegistry.GetOpenBucket(bucket);
        if (targetBucket is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var stream = targetBucket.OpenSnapshotStream();
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.OctetStream;
        if (stream.CanSeek)
            ctx.Response.ContentLength = stream.Length;
        await stream.CopyToAsync(ctx.Response.Body, SohBuffer.BlockSize, ctx.RequestAborted);
    }

    private static async Task HandleGetIamDelta(
        HttpContext ctx,
        ClusterMembershipOptions options,
        IIdentityRegistry identityRegistry)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetAction(ActionClusterGetIamDelta);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var snapshot = identityRegistry.ExportSnapshot();
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            snapshot,
            GossipJsonContext.Default.IdentityReplicationSnapshot,
            ctx.RequestAborted);
    }

    private static async Task HandleGetUploadSession(
        string sessionId,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IChunkStager chunkStager)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterGetUploadSession, key: sessionId);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var snapshot = chunkStager.GetLocalSessionSnapshot(sessionId);
        if (snapshot is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            snapshot,
            GossipJsonContext.Default.UploadSessionSnapshot,
            ctx.RequestAborted);
    }

    private static Task HandleDeleteUploadSession(
        string sessionId,
        HttpContext ctx,
        ClusterMembershipOptions options,
        IChunkStager chunkStager)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetTarget(ActionClusterDeleteUploadSession, key: sessionId);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var deleted = chunkStager.DeleteLocalSession(sessionId);
        ctx.Response.StatusCode = deleted ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static async Task HandleGetGcReferences(
        HttpContext ctx,
        ClusterMembershipOptions options,
        IBucketRegistry bucketRegistry,
        IChunkStager chunkStager,
        IBlobMirrorQueue mirrorQueue)
    {
        RequestTrace.SetContext(protocol: VesselProtocols.Cluster);
        RequestTrace.SetAction(ActionClusterGetGcReferences);

        if (!IsAuthorized(ctx, options))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var shas = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var sha in bucketRegistry.AllReferencedBlobs(ctx.RequestAborted))
        {
            if (!string.IsNullOrEmpty(sha))
                shas.Add(sha);
        }
        foreach (var sha in chunkStager.EnumerateInFlightChunkShas())
        {
            if (!string.IsNullOrEmpty(sha))
                shas.Add(sha);
        }
        foreach (var sha in mirrorQueue.EnumeratePendingShas())
        {
            if (!string.IsNullOrEmpty(sha))
                shas.Add(sha);
        }

        var response = new ClusterGcReferencesResponse([.. shas]);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            response,
            GossipJsonContext.Default.ClusterGcReferencesResponse,
            ctx.RequestAborted);
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
