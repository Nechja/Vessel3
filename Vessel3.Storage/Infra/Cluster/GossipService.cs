namespace Vessel3.Storage;

internal sealed partial class GossipService(
    ClusterMembershipOptions options,
    IClusterMembership membership,
    IGossipTransport transport,
    ILogger<GossipService>? log = null) : BackgroundService
{
    public async Task<int> RunCycle(CancellationToken ct = default)
    {
        var targets = SwimProtocol.SelectTargets(options, membership.Peers);
        var exchanged = 0;

        foreach (var (endpoint, knownNodeId) in targets)
        {
            var outbound = membership.BuildOutboundMessage();
            var result = await transport.ExchangeGossip(endpoint, outbound, ct);
            if (result.TryGetValue(out var reply, out _))
            {
                membership.MergeGossip(reply);
                exchanged++;
                continue;
            }

            if (knownNodeId is not null)
                membership.MarkPeerSuspect(knownNodeId);
        }

        membership.SweepTimeouts();
        return exchanged;
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
                await RunCycle(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (log is not null)
                    LogCycleFailed(log, ex);
            }
        }

        membership.Leave();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "gossip cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}
