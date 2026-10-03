using Microsoft.Extensions.Logging;
using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Domain;

public sealed partial class ServerReconciler(IKubernetesPort k8s, ILogger<ServerReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(VesselServerCustomResource server, CancellationToken ct = default)
    {
        var serverNs = string.IsNullOrEmpty(server.Metadata.Namespace) ? "default" : server.Metadata.Namespace;
        var serverName = server.Metadata.Name;
        var secretName = string.IsNullOrEmpty(server.Spec.Auth.AdminSecretName)
            ? $"{serverName}-admin-creds"
            : server.Spec.Auth.AdminSecretName;

        var credsResult = await k8s.EnsureServerSecret(serverNs, secretName, ct);
        if (!credsResult.TryGetValue(out var credentials, out var credsErr))
        {
            LogSecretError(logger, secretName, credsErr.Message);
            await UpdateStatus(serverNs, serverName, PhaseNames.Error, null, secretName, credsErr.Message, ct);
            return ReconciliationOutcome.Failure(credsErr.Message);
        }

        var workloadResult = await k8s.ReconcileServerWorkload(server, credentials, ct);
        if (workloadResult is Result.Failure workloadFailure)
        {
            LogWorkloadError(logger, serverName, workloadFailure.Error.Message);
            await UpdateStatus(serverNs, serverName, PhaseNames.Error, null, secretName, workloadFailure.Error.Message, ct);
            return ReconciliationOutcome.Failure(workloadFailure.Error.Message);
        }

        var endpoint = $"http://{serverName}.{serverNs}.svc:{server.Spec.Service.Port}";
        await UpdateStatus(serverNs, serverName, PhaseNames.Ready, endpoint, secretName, null, ct);
        return ReconciliationOutcome.Success();
    }

    private async Task UpdateStatus(
        string @namespace,
        string name,
        string phase,
        string? endpoint,
        string adminSecret,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new VesselServerStatus
        {
            Phase = phase,
            Endpoint = endpoint,
            AdminSecret = adminSecret,
            ReadyReplicas = phase == PhaseNames.Ready ? 1 : 0,
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

        await k8s.UpdateServerStatus(@namespace, name, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to ensure server secret {Secret}: {Error}")]
    private static partial void LogSecretError(ILogger logger, string secret, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to reconcile server workload {Server}: {Error}")]
    private static partial void LogWorkloadError(ILogger logger, string server, string error);
}
