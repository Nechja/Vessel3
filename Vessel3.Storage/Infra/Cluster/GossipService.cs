namespace Vessel3.Storage;

internal sealed partial class GossipService(
    GossipCoordinator coordinator,
    IClusterMembership membership,
    ClusterMembershipOptions options,
    ILogger<GossipService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsClusterEnabled || options.GossipInterval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(options.GossipInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.RunCycle(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCycleFailed(log, ex);
            }
        }

        membership.Leave();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "gossip cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}
