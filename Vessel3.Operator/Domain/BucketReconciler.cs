using Microsoft.Extensions.Logging;
using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Domain;

public sealed partial class BucketReconciler(
    IVesselPortFactory vesselFactory,
    IKubernetesPort k8s,
    ILogger<BucketReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(VesselBucketCustomResource bucketCr, CancellationToken ct = default)
    {
        var bucketNs = string.IsNullOrEmpty(bucketCr.Metadata.Namespace) ? "default" : bucketCr.Metadata.Namespace;
        var crName = bucketCr.Metadata.Name;
        var serverNs = string.IsNullOrEmpty(bucketCr.Spec.ServerRef.Namespace) ? bucketNs : bucketCr.Spec.ServerRef.Namespace;
        var serverName = bucketCr.Spec.ServerRef.Name;
        var bucketName = bucketCr.Spec.BucketName;

        var clientResult = await vesselFactory.CreateForServer(serverNs, serverName, ct);
        if (!clientResult.TryGetValue(out var vessel, out var clientErr))
        {
            LogConnectionError(logger, serverName, clientErr.Message);
            await UpdateStatus(bucketNs, crName, PhaseNames.Error, 0, 0, clientErr.Message, ct);
            return ReconciliationOutcome.Failure(clientErr.Message);
        }

        using (vessel)
        {
            var ensureResult = await vessel.EnsureBucket(bucketName, ct);
            if (ensureResult is Result.Failure ensureFailure)
            {
                LogEnsureError(logger, bucketName, ensureFailure.Error.Message);
                await UpdateStatus(bucketNs, crName, PhaseNames.Error, 0, 0, ensureFailure.Error.Message, ct);
                return ReconciliationOutcome.Failure(ensureFailure.Error.Message);
            }

            if (!string.IsNullOrEmpty(bucketCr.Spec.Versioning) && !string.Equals(bucketCr.Spec.Versioning, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                await vessel.ConfigureVersioning(bucketName, bucketCr.Spec.Versioning, ct);
            }

            if (bucketCr.Spec.Website is { } site)
            {
                var websiteDto = new BucketWebsiteDto(site.IndexDocument, site.ErrorDocument);
                await vessel.ConfigureWebsite(bucketName, websiteDto, ct);
            }

            var statsResult = await vessel.FetchBucketStats(bucketName, ct);
            var (sizeBytes, objectCount) = statsResult.TryGetValue(out var stats, out _)
                ? (stats.SizeBytes, stats.ObjectCount)
                : (0L, 0L);

            await UpdateStatus(bucketNs, crName, PhaseNames.Ready, sizeBytes, objectCount, null, ct);
            return ReconciliationOutcome.Success();
        }
    }

    private async Task UpdateStatus(
        string @namespace,
        string name,
        string phase,
        long sizeBytes,
        long objectCount,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new VesselBucketStatus
        {
            Phase = phase,
            SizeBytes = sizeBytes,
            ObjectCount = objectCount,
            Conditions = [
                new()
                {
                    Type = ConditionTypes.Ready,
                    Status = phase == PhaseNames.Ready ? ConditionTypes.StatusTrue : ConditionTypes.StatusFalse,
                    Reason = phase == PhaseNames.Ready ? ConditionTypes.ReasonReconciled : ConditionTypes.ReasonReconcileFailed,
                    Message = errorMessage ?? string.Empty
                }
            ]
        };

        await k8s.UpdateBucketStatus(@namespace, name, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not connect to Vessel server {Server}: {Error}")]
    private static partial void LogConnectionError(ILogger logger, string server, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to ensure bucket {Bucket}: {Error}")]
    private static partial void LogEnsureError(ILogger logger, string bucket, string error);
}
