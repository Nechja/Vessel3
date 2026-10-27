using Microsoft.Extensions.Logging;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Vessel3.Primitives;

namespace Vessel3.Operator.Domain;

public sealed partial class ServerReconciler(IKubernetesPort k8s, ILogger<ServerReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(ServerDeclaration server, CancellationToken ct = default)
    {
        var secretName = string.IsNullOrEmpty(server.AdminSecretName)
            ? $"{server.Identity.Name}{ServerDefaults.AdminCredsSuffix}"
            : server.AdminSecretName;

        var credsResult = await k8s.EnsureServerSecret(server.Identity, secretName, ct);
        if (!credsResult.TryGetValue(out var credentials, out var credsErr))
        {
            LogSecretError(logger, secretName, credsErr.Message);
            await UpdateStatus(server.Identity, PhaseNames.Error, null, secretName, 0, credsErr.Message, ct);
            return ReconciliationOutcome.Failure(credsErr.Message);
        }

        var workloadResult = await k8s.ReconcileServerWorkload(server, credentials, ct);
        if (workloadResult is Result.Failure workloadFailure)
        {
            LogWorkloadError(logger, server.Identity.Name, workloadFailure.Error.Message);
            await UpdateStatus(server.Identity, PhaseNames.Error, null, secretName, 0, workloadFailure.Error.Message, ct);
            return ReconciliationOutcome.Failure(workloadFailure.Error.Message);
        }

        var endpoint = server.Identity.BuildClusterEndpoint(server.Port);
        await UpdateStatus(server.Identity, PhaseNames.Ready, endpoint, secretName, server.Replicas, null, ct);
        return ReconciliationOutcome.Success();
    }

    private async Task UpdateStatus(
        ResourceIdentity id,
        string phase,
        string? endpoint,
        string adminSecret,
        int readyReplicas,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new ServerResourceStatus(
            phase,
            endpoint,
            adminSecret,
            ReadyReplicas: readyReplicas,
            Conditions: [ResourceCondition.Ready(phase == PhaseNames.Ready, errorMessage)]);

        await k8s.UpdateServerStatus(id, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to ensure secret {SecretName}: {Error}")]
    private static partial void LogSecretError(ILogger logger, string secretName, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to reconcile workload for {ServerName}: {Error}")]
    private static partial void LogWorkloadError(ILogger logger, string serverName, string error);
}
