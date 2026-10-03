using Microsoft.Extensions.Logging;
using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Domain;

public sealed partial class UserReconciler(
    IVesselPortFactory vesselFactory,
    IKubernetesPort k8s,
    ILogger<UserReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(UserDeclaration user, CancellationToken ct = default)
    {
        var clientResult = await vesselFactory.CreateForServer(user.ServerReference, ct);
        if (!clientResult.TryGetValue(out var vessel, out var clientErr))
        {
            LogConnectionError(logger, user.ServerReference.Name, clientErr.Message);
            await UpdateStatus(user.Identity, PhaseNames.Error, null, null, clientErr.Message, ct);
            return ReconciliationOutcome.Failure(clientErr.Message);
        }

        using (vessel)
        {
            var userResult = await vessel.EnsureUser(user.Username, user.Role, ct);
            if (userResult is Result.Failure userFailure)
            {
                LogUserError(logger, user.Username, userFailure.Error.Message);
                await UpdateStatus(user.Identity, PhaseNames.Error, null, null, userFailure.Error.Message, ct);
                return ReconciliationOutcome.Failure(userFailure.Error.Message);
            }

            var keyResult = await vessel.IssueAccessKey(user.Username, $"Managed by Vessel3 Operator ({user.Identity.Name})", ct);
            if (!keyResult.TryGetValue(out var accessKey, out var keyErr))
            {
                LogKeyError(logger, user.Username, keyErr.Message);
                await UpdateStatus(user.Identity, PhaseNames.Error, null, null, keyErr.Message, ct);
                return ReconciliationOutcome.Failure(keyErr.Message);
            }

            string? secretName = null;
            if (user.SecretOutput is { } secret)
            {
                secretName = secret.Name;
                var endpoint = $"http://{user.ServerReference.Name}.{user.ServerReference.Namespace}.svc:9000";
                var secretData = new Dictionary<string, string>
                {
                    [secret.AccessKeyField] = accessKey.AccessKeyId,
                    [secret.SecretKeyField] = accessKey.SecretAccessKey,
                    [secret.EndpointField] = endpoint,
                    [secret.RegionField] = "us-east-1"
                };

                var writeResult = await k8s.WriteUserSecret(secret.Namespace, secret.Name, secretData, ct);
                if (writeResult is Result.Failure writeFailure)
                {
                    LogSecretWriteError(logger, secret.Name, writeFailure.Error.Message);
                    await UpdateStatus(user.Identity, PhaseNames.Error, null, null, writeFailure.Error.Message, ct);
                    return ReconciliationOutcome.Failure(writeFailure.Error.Message);
                }
            }

            await UpdateStatus(user.Identity, PhaseNames.Ready, accessKey.AccessKeyId, secretName, null, ct);
            return ReconciliationOutcome.Success();
        }
    }

    private async Task UpdateStatus(
        ResourceIdentity id,
        string phase,
        string? userId,
        string? secretRef,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new UserStatus(
            phase,
            userId,
            secretRef,
            errorMessage,
            Conditions: [
                new(
                    ConditionTypes.Ready,
                    phase == PhaseNames.Ready ? ConditionTypes.StatusTrue : ConditionTypes.StatusFalse,
                    phase == PhaseNames.Ready ? ConditionTypes.ReasonReconciled : ConditionTypes.ReasonReconcileFailed,
                    errorMessage ?? string.Empty
                )
            ]);

        await k8s.UpdateUserStatus(id, status, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to connect to Vessel server {ServerName}: {Error}")]
    private static partial void LogConnectionError(ILogger logger, string serverName, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to ensure IAM user {Username}: {Error}")]
    private static partial void LogUserError(ILogger logger, string username, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to issue access key for user {Username}: {Error}")]
    private static partial void LogKeyError(ILogger logger, string username, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to write user credentials secret {SecretName}: {Error}")]
    private static partial void LogSecretWriteError(ILogger logger, string secretName, string error);
}
