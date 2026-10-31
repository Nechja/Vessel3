using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;
using Vessel3.Storage.Lifecycle;

namespace Vessel3.Protocols.Native.Adapters;

internal sealed class NativeAdminAdapter(
    IGarbageCollector gc,
    ILifecycleSweeper sweeper,
    IServerLogBuffer buffer,
    IVolumeRegistry volumes,
    IClusterMembership membership,
    ClusterMembershipOptions clusterOptions,
    ServerRegion region,
    IBucketRegistry? buckets = null,
    IIdentityRegistry? identity = null,
    IBlobMirrorQueue? mirrorQueue = null)
{
    public async Task<IResult> RunGc(HttpContext ctx)
    {
        RequestTrace.SetTarget(NativeConstants.TraceAdminGc);
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError(NativeConstants.AdminRoleRequiredMessage).ToHttpResult();
        }

        var blobAge = long.TryParse(ctx.Request.Query[VesselQueryParams.MinBlobAgeSec], out var b) ? b : 3600;
        var uploadAge = long.TryParse(ctx.Request.Query[VesselQueryParams.MinUploadAgeSec], out var u) ? u : 604800;
        var report = await gc.Run(TimeSpan.FromSeconds(blobAge), TimeSpan.FromSeconds(uploadAge));
        return Results.Json(new GcReportDto(report.BlobsDeleted, report.UploadsReaped), NativeJsonContext.Default.GcReportDto);
    }

    public IResult RunSweep(HttpContext ctx)
    {
        RequestTrace.SetTarget(NativeConstants.TraceAdminSweep);
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError(NativeConstants.AdminRoleRequiredMessage).ToHttpResult();
        }

        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue(VesselQueryParams.Now, out var nowRaw) &&
            DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            now = parsed;
        }

        var report = sweeper.Run(now);
        return Results.Json(new SweepReportDto(report.Expired, report.MarkersReaped), NativeJsonContext.Default.SweepReportDto);
    }

    public IResult GetLogs(HttpContext ctx)
    {
        RequestTrace.SetTarget(NativeConstants.TraceAdminLogs);
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError(NativeConstants.AdminRoleRequiredMessage).ToHttpResult();
        }

        var limit = int.TryParse(ctx.Request.Query[VesselQueryParams.Limit], out var l) ? l : 100;
        var level = ctx.Request.Query.TryGetValue(VesselQueryParams.Level, out var lvl) ? lvl.ToString() : null;
        var protocol = ctx.Request.Query.TryGetValue(VesselQueryParams.Protocol, out var proto) ? proto.ToString() : null;

        var logs = buffer.GetRecent(limit, level, protocol);
        List<ServerLogEntryDto> dtos = [.. logs.Select(entry => new ServerLogEntryDto(
            entry.Id,
            entry.Timestamp,
            entry.Level,
            entry.Source,
            entry.Message,
            entry.Protocol,
            entry.Action,
            entry.Subject,
            entry.Actor,
            entry.StatusCode,
            entry.DurationMs,
            entry.TraceId,
            entry.ErrorDetails))];

        return Results.Json(dtos, NativeJsonContext.Default.ListServerLogEntryDto);
    }

    public IResult ClearLogs(HttpContext ctx)
    {
        RequestTrace.SetTarget(NativeConstants.TraceAdminClearLogs);
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError(NativeConstants.AdminRoleRequiredMessage).ToHttpResult();
        }

        buffer.Clear();
        return Results.NoContent();
    }

    public IResult GetClusterStatus(HttpContext ctx)
    {
        RequestTrace.SetTarget(NativeConstants.TraceAdminClusterStatus);
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError(NativeConstants.AdminRoleRequiredMessage).ToHttpResult();
        }

        var defaultIngestId = volumes.DefaultIngestVolume.Id;
        var allVolumes = volumes.Volumes;
        var volumeDtos = new List<VolumeDto>(allVolumes.Count);
        var localDiskMetrics = new Dictionary<string, (long? Capacity, long? Available)>(StringComparer.Ordinal);

        for (var i = 0; i < allVolumes.Count; i++)
        {
            var vol = allVolumes[i];
            var (capacity, available) = ResolveDiskSpace(vol);
            if (!vol.IsRemote)
            {
                localDiskMetrics[vol.Id] = (capacity, available);
            }

            volumeDtos.Add(new VolumeDto(
                vol.Id,
                vol.Path,
                vol.Pool,
                FormatCapabilities(vol.Capabilities),
                vol.Status.ToString(),
                vol.IsRemote,
                vol.IsWritable,
                vol.IsReadable,
                string.Equals(vol.Id, defaultIngestId, StringComparison.Ordinal),
                capacity,
                available,
                vol.NodeId ?? clusterOptions.NodeId));
        }

        var localNode = MapPeer(membership.LocalNode, localDiskMetrics);
        var peers = membership.Peers;
        var peerDtos = new List<ClusterPeerDto>(peers.Count);
        for (var i = 0; i < peers.Count; i++)
        {
            peerDtos.Add(MapPeer(peers[i], null));
        }

        var iamGen = identity?.Generation ?? 0L;
        var pendingBlobs = mirrorQueue?.PendingCount ?? 0;
        var bucketWatermarks = BuildBucketWatermarks(buckets);

        var status = new ClusterStatusDto(
            clusterOptions.NodeId,
            region.Value,
            clusterOptions.IsClusterEnabled,
            localNode,
            peerDtos,
            volumeDtos,
            iamGen,
            pendingBlobs,
            bucketWatermarks);

        return Results.Json(status, NativeJsonContext.Default.ClusterStatusDto);
    }

    private static List<BucketReplicationDto> BuildBucketWatermarks(IBucketRegistry? buckets)
    {
        if (buckets is null)
        {
            return [];
        }

        var entries = buckets.GetCatalogEntries();
        var list = new List<BucketReplicationDto>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.DeletedAtUnixMs > 0)
            {
                continue;
            }

            var cfg = entry.Config;
            var access = cfg?.Access ?? BucketAccess.Private;
            list.Add(new BucketReplicationDto(
                entry.Name,
                entry.AppliedSeq,
                entry.FirstAvailableLogSeq,
                (cfg?.Versioning ?? VersioningStatus.Suspended).ToString(),
                access.PublicRead,
                access.ReadOnly));
        }

        return list;
    }

    private static ClusterPeerDto MapPeer(
        PeerNodeState peer,
        Dictionary<string, (long? Capacity, long? Available)>? localMetrics)
    {
        var advertised = new List<AdvertisedVolumeDto>(peer.Volumes.Count);
        for (var i = 0; i < peer.Volumes.Count; i++)
        {
            var vol = peer.Volumes[i];
            var capacity = vol.CapacityBytes;
            var available = vol.AvailableBytes;
            if ((capacity is null || available is null)
                && localMetrics is not null
                && localMetrics.TryGetValue(vol.VolumeId, out var resolved))
            {
                capacity ??= resolved.Capacity;
                available ??= resolved.Available;
            }

            advertised.Add(new AdvertisedVolumeDto(
                vol.VolumeId,
                vol.Pool,
                FormatCapabilities(vol.Capabilities),
                vol.Status.ToString(),
                capacity,
                available));
        }

        return new ClusterPeerDto(
            peer.NodeId,
            peer.Endpoint,
            peer.Incarnation,
            peer.Status.ToString(),
            peer.LastSeenUtc,
            advertised);
    }

    private static (long? Capacity, long? Available) ResolveDiskSpace(StorageVolume vol)
    {
        if (vol.IsRemote || (vol.CapacityBytes is not null && vol.AvailableBytes is not null))
        {
            return (vol.CapacityBytes, vol.AvailableBytes);
        }

        try
        {
            var fullPath = Path.GetFullPath(vol.BlobsRoot);
            if (!Directory.Exists(fullPath))
            {
                return (vol.CapacityBytes, vol.AvailableBytes);
            }

            var drive = new DriveInfo(fullPath);
            if (!drive.IsReady)
            {
                return (vol.CapacityBytes, vol.AvailableBytes);
            }

            return (vol.CapacityBytes ?? drive.TotalSize, vol.AvailableBytes ?? drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (vol.CapacityBytes, vol.AvailableBytes);
        }
    }

    private static IReadOnlyList<string> FormatCapabilities(VolumeCapabilities capabilities)
    {
        if (capabilities is VolumeCapabilities.None)
        {
            return [nameof(VolumeCapabilities.None)];
        }

        var list = new List<string>(5);
        if ((capabilities & VolumeCapabilities.Ingest) != 0) list.Add(nameof(VolumeCapabilities.Ingest));
        if ((capabilities & VolumeCapabilities.OnDemand) != 0) list.Add(nameof(VolumeCapabilities.OnDemand));
        if ((capabilities & VolumeCapabilities.ReadOnly) != 0) list.Add(nameof(VolumeCapabilities.ReadOnly));
        if ((capabilities & VolumeCapabilities.Mirrored) != 0) list.Add(nameof(VolumeCapabilities.Mirrored));
        if ((capabilities & VolumeCapabilities.Remote) != 0) list.Add(nameof(VolumeCapabilities.Remote));
        return list;
    }
}
