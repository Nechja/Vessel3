using Microsoft.Extensions.Logging;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Vessel3.Primitives;

namespace Vessel3.Operator.Domain;

public sealed partial class BucketReconciler(
    IVesselPortFactory vesselFactory,
    IKubernetesPort k8s,
    ILogger<BucketReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(BucketDeclaration bucket, CancellationToken ct = default)
    {
        var clientResult = await vesselFactory.CreateForServer(bucket.ServerReference, ct);
        if (!clientResult.TryGetValue(out var vessel, out var clientErr))
        {
            LogConnectionError(logger, bucket.ServerReference.Name, clientErr.Message);
            await UpdateStatus(bucket.Identity, PhaseNames.Error, 0, 0, clientErr.Message, ct);
            return ReconciliationOutcome.Failure(clientErr.Message);
        }

        using (vessel)
        {
            var ensureResult = await vessel.EnsureBucket(bucket.BucketName, ct);
            if (ensureResult is Result.Failure ensureFailure)
            {
                LogEnsureError(logger, bucket.BucketName, ensureFailure.Error.Message);
                await UpdateStatus(bucket.Identity, PhaseNames.Error, 0, 0, ensureFailure.Error.Message, ct);
                return ReconciliationOutcome.Failure(ensureFailure.Error.Message);
            }

            if (!string.IsNullOrEmpty(bucket.Versioning) && !string.Equals(bucket.Versioning, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                await vessel.ConfigureVersioning(bucket.BucketName, bucket.Versioning, ct);
            }

            if (bucket.Website is { } site)
            {
                await vessel.ConfigureWebsite(bucket.BucketName, site, ct);
            }

            if (!string.IsNullOrEmpty(bucket.Access) && !string.Equals(bucket.Access, "private", StringComparison.OrdinalIgnoreCase))
            {
                await vessel.ConfigureAccess(bucket.BucketName, bucket.Access, ct);
            }

            var statsResult = await vessel.FetchBucketStats(bucket.BucketName, ct);
            var (sizeBytes, objectCount) = statsResult.TryGetValue(out var stats, out _)
                ? (stats.SizeBytes, stats.ObjectCount)
                : (0L, 0L);

            await UpdateStatus(bucket.Identity, PhaseNames.Ready, sizeBytes, objectCount, null, ct);
            return ReconciliationOutcome.Success();
        }
    }

    private async Task UpdateStatus(
        ResourceIdentity id,
        string phase,
        long sizeBytes,
        long objectCount,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new BucketResourceStatus(
            phase,
            sizeBytes,
            objectCount,
            errorMessage,
            Conditions: [ResourceCondition.Ready(phase == PhaseNames.Ready, errorMessage)]);

        await k8s.UpdateBucketStatus(id, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to connect to Vessel server {ServerName}: {Error}")]
    private static partial void LogConnectionError(ILogger logger, string serverName, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to ensure bucket {BucketName}: {Error}")]
    private static partial void LogEnsureError(ILogger logger, string bucketName, string error);
}
