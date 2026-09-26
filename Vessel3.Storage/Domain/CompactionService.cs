namespace Vessel3.Storage;

internal sealed record CompactionServiceOptions(TimeSpan Interval, long ThresholdBytes);

internal sealed partial class CompactionService(
    ICompactor compactor,
    CompactionServiceOptions options,
    ILogger<CompactionService> log) : BackgroundService
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
                var report = compactor.Run(options.ThresholdBytes);
                if (report.BucketsCompacted > 0 || report.Failed > 0)
                    LogCompacted(log, report.BucketsCompacted, report.Failed, report.LogBytesBefore, report.LogBytesAfter);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogFailed(log, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "compaction: buckets={Buckets} failed={Failed} log_bytes {Before} -> {After}")]
    private static partial void LogCompacted(ILogger logger, int buckets, int failed, long before, long after);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "compaction failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
