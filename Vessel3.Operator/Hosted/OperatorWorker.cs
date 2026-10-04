using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Hosted;

public sealed partial class OperatorWorker(
    IKubernetesPort k8s,
    ServerReconciler serverReconciler,
    BucketReconciler bucketReconciler,
    UserReconciler userReconciler,
    WebhookReconciler webhookReconciler,
    ILogger<OperatorWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileBatch(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogError(logger, ex);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogStopped(logger);
    }

    private async Task ReconcileBatch(CancellationToken ct)
    {
        var servers = await k8s.ListServers(ct);
        foreach (var server in servers)
        {
            await serverReconciler.Reconcile(server, ct);
        }

        var buckets = await k8s.ListBuckets(ct);
        foreach (var bucket in buckets)
        {
            await bucketReconciler.Reconcile(bucket, ct);
        }

        var users = await k8s.ListUsers(ct);
        foreach (var user in users)
        {
            await userReconciler.Reconcile(user, ct);
        }

        var webhooks = await k8s.ListWebhooks(ct);
        foreach (var webhook in webhooks)
        {
            await webhookReconciler.Reconcile(webhook, ct);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Vessel3 Operator loop started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Vessel3 Operator loop stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in operator reconciliation cycle")]
    private static partial void LogError(ILogger logger, Exception ex);
}
