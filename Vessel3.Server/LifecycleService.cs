using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vessel3.Server;

internal sealed record LifecycleServiceOptions(TimeSpan Interval);

internal sealed partial class LifecycleService(
    ILifecycleSweeper sweeper,
    LifecycleServiceOptions options,
    ILogger<LifecycleService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(options.Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
                var report = sweeper.Run(DateTimeOffset.UtcNow);
                if (report.Expired > 0 || report.MarkersReaped > 0)
                    LogSwept(log, report.Expired, report.MarkersReaped);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogFailed(log, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "lifecycle sweep: expired={Expired} markers_reaped={Reaped}")]
    private static partial void LogSwept(ILogger logger, int expired, int reaped);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "lifecycle sweep failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
