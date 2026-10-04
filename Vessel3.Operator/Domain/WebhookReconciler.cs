using Microsoft.Extensions.Logging;
using Vessel3.Client;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Domain;

public sealed partial class WebhookReconciler(
    IVesselPortFactory vesselFactory,
    IKubernetesPort k8s,
    ILogger<WebhookReconciler> logger)
{
    public async Task<ReconciliationOutcome> Reconcile(WebhookDeclaration webhook, CancellationToken ct = default)
    {
        var clientResult = await vesselFactory.CreateForServer(webhook.ServerReference, ct);
        if (!clientResult.TryGetValue(out var vessel, out var clientErr))
        {
            LogConnectionError(logger, webhook.ServerReference.Name, clientErr.Message);
            await UpdateStatus(webhook.Identity, PhaseNames.Error, null, null, null, clientErr.Message, ct);
            return ReconciliationOutcome.Failure(clientErr.Message);
        }

        using var clientScope = vessel;

        string? secretValue = null;
        if (webhook.SecretRef is { } secRef)
        {
            var secretResult = await k8s.FetchSecretValue(secRef.Namespace, secRef.Name, secRef.Key, ct);
            if (!secretResult.TryGetValue(out var resolvedSecret, out var secErr))
            {
                LogSecretError(logger, secRef.Name, secErr.Message);
                await UpdateStatus(webhook.Identity, PhaseNames.Error, null, null, null, secErr.Message, ct);
                return ReconciliationOutcome.Failure(secErr.Message);
            }

            secretValue = resolvedSecret;
        }

        var listResult = await vessel.ListWebhooks(ct);
        if (!listResult.TryGetValue(out var existingHooks, out var listErr))
        {
            LogWebhookError(logger, webhook.Name, listErr.Message);
            await UpdateStatus(webhook.Identity, PhaseNames.Error, null, null, null, listErr.Message, ct);
            return ReconciliationOutcome.Failure(listErr.Message);
        }

        var existing = existingHooks.FirstOrDefault(h => string.Equals(h.Name, webhook.Name, StringComparison.OrdinalIgnoreCase));
        var desiredEvents = webhook.EventFilters is { Count: > 0 } ef ? ef : ["*"];

        if (existing is null)
        {
            var createDto = new CreateWebhookDto(
                webhook.Name,
                webhook.Url,
                secretValue,
                desiredEvents,
                webhook.ResourceFilters,
                webhook.Active);

            var createResult = await vessel.EnsureWebhook(createDto, ct);
            if (!createResult.TryGetValue(out var created, out var createErr))
            {
                LogWebhookError(logger, webhook.Name, createErr.Message);
                await UpdateStatus(webhook.Identity, PhaseNames.Error, null, null, null, createErr.Message, ct);
                return ReconciliationOutcome.Failure(createErr.Message);
            }

            await UpdateStatus(webhook.Identity, PhaseNames.Ready, created.Id, created.LastTriggeredAt, created.LastStatusCode, null, ct);
            return ReconciliationOutcome.Success();
        }

        var needsUpdate = existing.Url != webhook.Url
            || (secretValue is not null && existing.Secret != secretValue)
            || existing.Active != webhook.Active
            || !AreListsEqual(existing.EventFilters, desiredEvents)
            || !AreNullableListsEqual(existing.ResourceFilters, webhook.ResourceFilters);

        if (needsUpdate)
        {
            var updateDto = new UpdateWebhookDto(
                webhook.Name,
                webhook.Url,
                secretValue ?? existing.Secret,
                desiredEvents,
                webhook.ResourceFilters,
                webhook.Active);

            var updateResult = await vessel.UpdateWebhook(existing.Id, updateDto, ct);
            if (!updateResult.TryGetValue(out var updated, out var updateErr))
            {
                LogWebhookError(logger, webhook.Name, updateErr.Message);
                await UpdateStatus(webhook.Identity, PhaseNames.Error, existing.Id, existing.LastTriggeredAt, existing.LastStatusCode, updateErr.Message, ct);
                return ReconciliationOutcome.Failure(updateErr.Message);
            }

            await UpdateStatus(webhook.Identity, PhaseNames.Ready, updated.Id, updated.LastTriggeredAt, updated.LastStatusCode, null, ct);
            return ReconciliationOutcome.Success();
        }

        await UpdateStatus(webhook.Identity, PhaseNames.Ready, existing.Id, existing.LastTriggeredAt, existing.LastStatusCode, null, ct);
        return ReconciliationOutcome.Success();
    }

    private async Task UpdateStatus(
        ResourceIdentity id,
        string phase,
        string? webhookId,
        DateTimeOffset? lastTriggeredAt,
        int? lastStatusCode,
        string? errorMessage,
        CancellationToken ct)
    {
        var status = new WebhookResourceStatus(
            phase,
            webhookId,
            lastTriggeredAt,
            lastStatusCode,
            errorMessage,
            Conditions: [ResourceCondition.Ready(phase == PhaseNames.Ready, errorMessage)]);

        await k8s.UpdateWebhookStatus(id, status, ct);
    }

    private static bool AreListsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreNullableListsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        return AreListsEqual(a, b);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to connect to Vessel server {ServerName}: {Error}")]
    private static partial void LogConnectionError(ILogger logger, string serverName, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to reconcile webhook {WebhookName}: {Error}")]
    private static partial void LogWebhookError(ILogger logger, string webhookName, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to read secret {SecretName}: {Error}")]
    private static partial void LogSecretError(ILogger logger, string secretName, string error);
}
