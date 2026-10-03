using Microsoft.Extensions.Logging;
using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Domain;

public sealed partial class UserReconciler(
    IVesselPortFactory vesselFactory,
    IKubernetesPort k8s,
    ILogger<UserReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(VesselUserCustomResource userCr, CancellationToken ct = default)
    {
        var userNs = string.IsNullOrEmpty(userCr.Metadata.Namespace) ? "default" : userCr.Metadata.Namespace;
        var crName = userCr.Metadata.Name;
        var serverNs = string.IsNullOrEmpty(userCr.Spec.ServerRef.Namespace) ? userNs : userCr.Spec.ServerRef.Namespace;
        var serverName = userCr.Spec.ServerRef.Name;
        var username = userCr.Spec.Username;
        var role = string.IsNullOrEmpty(userCr.Spec.Role) ? "Member" : userCr.Spec.Role;

        var clientResult = await vesselFactory.CreateForServer(serverNs, serverName, ct);
        if (!clientResult.TryGetValue(out var vessel, out var clientErr))
        {
            LogConnectionError(logger, serverName, clientErr.Message);
            await UpdateStatus(userNs, crName, PhaseNames.Error, null, null, clientErr.Message, ct);
            return ReconciliationOutcome.Failure(clientErr.Message);
        }

        using (vessel)
        {
            var userResult = await vessel.EnsureUser(username, role, ct);
            if (userResult is Result.Failure userFailure)
            {
                LogUserError(logger, username, userFailure.Error.Message);
                await UpdateStatus(userNs, crName, PhaseNames.Error, null, null, userFailure.Error.Message, ct);
                return ReconciliationOutcome.Failure(userFailure.Error.Message);
            }

            var keyResult = await vessel.IssueAccessKey(username, $"Managed by Vessel3 Operator ({crName})", ct);
            if (!keyResult.TryGetValue(out var accessKey, out var keyErr))
            {
                LogKeyError(logger, username, keyErr.Message);
                await UpdateStatus(userNs, crName, PhaseNames.Error, null, null, keyErr.Message, ct);
                return ReconciliationOutcome.Failure(keyErr.Message);
            }

            var targetSecretNs = string.IsNullOrEmpty(userCr.Spec.WriteSecret.SecretNamespace)
                ? userNs
                : userCr.Spec.WriteSecret.SecretNamespace;
            var targetSecretName = userCr.Spec.WriteSecret.SecretName;
            var endpoint = $"http://{serverName}.{serverNs}.svc:9000";

            var mapping = userCr.Spec.WriteSecret.Keys;
            var secretData = new Dictionary<string, string>
            {
                [mapping.AccessKey] = accessKey.AccessKeyId,
                [mapping.SecretKey] = accessKey.SecretAccessKey,
                [mapping.Endpoint] = endpoint,
                [mapping.Region] = "us-east-1"
            };

            if (!string.IsNullOrEmpty(mapping.BucketName))
            {
                secretData["BUCKET_NAME"] = mapping.BucketName;
            }

            var writeResult = await k8s.WriteUserSecret(targetSecretNs, targetSecretName, secretData, ct);
            if (writeResult is Result.Failure writeFailure)
            {
                LogSecretError(logger, targetSecretName, targetSecretNs, writeFailure.Error.Message);
                await UpdateStatus(userNs, crName, PhaseNames.Error, null, null, writeFailure.Error.Message, ct);
                return ReconciliationOutcome.Failure(writeFailure.Error.Message);
            }

            await UpdateStatus(userNs, crName, PhaseNames.Ready, username, targetSecretName, null, ct);
            return ReconciliationOutcome.Success();
        }
    }

    private async Task UpdateStatus(
        string @namespace,
        string name,
        string phase,
        string? userId,
        string? secretRef,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new VesselUserStatus
        {
            Phase = phase,
            UserId = userId,
            SecretRef = secretRef,
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

        await k8s.UpdateUserStatus(@namespace, name, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not connect to Vessel server {Server}: {Error}")]
    private static partial void LogConnectionError(ILogger logger, string server, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to ensure user {Username}: {Error}")]
    private static partial void LogUserError(ILogger logger, string username, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to issue access key for user {Username}: {Error}")]
    private static partial void LogKeyError(ILogger logger, string username, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to write secret {Secret} in {Namespace}: {Error}")]
    private static partial void LogSecretError(ILogger logger, string secret, string @namespace, string error);
}
