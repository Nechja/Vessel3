namespace Vessel3.Storage;

internal sealed partial class VolumeMirrorService(
    ClusterMembershipOptions options,
    IVolumeRegistry volumeRegistry,
    IBlobMirrorQueue mirrorQueue,
    ILogger<VolumeMirrorService>? log = null) : BackgroundService
{
    public async Task<int> DrainPending(CancellationToken ct = default)
    {
        var mirroredCount = 0;
        while (mirrorQueue.TryDequeue(out var sha))
        {
            if (sha is null)
                continue;

            try
            {
                if (await TryMirrorBlob(sha, ct))
                    mirroredCount++;
            }
            finally
            {
                mirrorQueue.Complete(sha);
            }
        }
        return mirroredCount;
    }

    public async Task<bool> TryMirrorBlob(string sha, CancellationToken ct = default)
    {
        try
        {
            var missingTargets = await FindMissingTargets(sha, ct);
            if (missingTargets.Count == 0)
                return false;

            var sourceStream = await OpenFirstAvailableSource(sha, ct);
            if (sourceStream is null)
                return false;

            await using (sourceStream)
            {
                return await ReplicateToTargets(sourceStream, missingTargets, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (log is not null)
                LogMirrorFailed(log, sha, ex);
            return false;
        }
    }

    private async Task<List<IVolumeStorage>> FindMissingTargets(string sha, CancellationToken ct)
    {
        var targets = volumeRegistry.MirrorTargetStorages;
        if (targets.Count == 0)
            return [];

        var missingTargets = new List<IVolumeStorage>(targets.Count);
        foreach (var target in targets)
        {
            if (!await target.BlobExists(sha, ct))
                missingTargets.Add(target);
        }
        return missingTargets;
    }

    private async Task<Stream?> OpenFirstAvailableSource(string sha, CancellationToken ct)
    {
        foreach (var candidate in volumeRegistry.ReadPriorityReaders)
        {
            if ((await candidate.OpenBlob(sha, ct)).TryGetValue(out var s, out _))
                return s;
        }
        return null;
    }

    private static async Task<bool> ReplicateToTargets(
        Stream sourceStream,
        List<IVolumeStorage> missingTargets,
        CancellationToken ct)
    {
        var primaryTarget = missingTargets[0];
        var writeRes = await primaryTarget.WriteStagedBlob(sourceStream, null, ChecksumIntent.None, ct);
        if (!writeRes.TryGetValue(out var stored, out _))
            return false;

        for (var i = 1; i < missingTargets.Count; i++)
        {
            var openCopy = await primaryTarget.OpenBlob(stored.Sha, ct);
            if (!openCopy.TryGetValue(out var copyStream, out _))
                continue;

            await using (copyStream)
            {
                _ = await missingTargets[i].WriteStagedBlob(copyStream, stored.Size, ChecksumIntent.None, ct);
            }
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsClusterEnabled || options.GossipInterval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(options.GossipInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainPending(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "blob mirror failed for {Sha}")]
    private static partial void LogMirrorFailed(ILogger logger, string sha, Exception ex);
}
