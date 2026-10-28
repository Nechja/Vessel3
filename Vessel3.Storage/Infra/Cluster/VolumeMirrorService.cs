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
            if (sha is not null && await TryMirrorBlob(sha, ct))
                mirroredCount++;
        }
        return mirroredCount;
    }

    public async Task<bool> TryMirrorBlob(string sha, CancellationToken ct = default)
    {
        try
        {
            var targets = volumeRegistry.MirrorTargetStorages;
            if (targets.Count == 0)
                return false;

            List<IVolumeStorage>? missingTargets = null;
            foreach (var target in targets)
            {
                if (!await target.BlobExists(sha, ct))
                {
                    missingTargets ??= [];
                    missingTargets.Add(target);
                }
            }

            if (missingTargets is null || missingTargets.Count == 0)
                return false;

            Stream? sourceStream = null;
            foreach (var candidate in volumeRegistry.ReadPriorityReaders)
            {
                if (!(await candidate.OpenBlob(sha, ct)).TryGetValue(out var s, out _))
                    continue;

                sourceStream = s;
                break;
            }

            if (sourceStream is null)
                return false;

            var primaryTarget = missingTargets[0];
            await using (sourceStream)
            {
                var writeRes = await primaryTarget.WriteStagedBlob(sourceStream, null, ChecksumIntent.None, ct);
                if (!writeRes.TryGetValue(out var stored, out _))
                    return false;

                for (var i = 1; i < missingTargets.Count; i++)
                {
                    if ((await primaryTarget.OpenBlob(stored.Sha, ct)).TryGetValue(out var copyStream, out _))
                    {
                        await using (copyStream)
                        {
                            _ = await missingTargets[i].WriteStagedBlob(copyStream, stored.Size, ChecksumIntent.None, ct);
                        }
                    }
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (log is not null)
                LogMirrorFailed(log, sha, ex);
            return false;
        }
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
