using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Client;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Domain.Models;
using Xunit;

namespace Vessel3.Tests.Operator;

public sealed class WebhookReconcilerTests
{
    private readonly InMemoryVesselPortFactory vesselFactory = new();
    private readonly InMemoryKubernetesPort k8s = new();
    private readonly WebhookReconciler reconciler;

    public WebhookReconcilerTests()
    {
        reconciler = new WebhookReconciler(vesselFactory, k8s, NullLogger<WebhookReconciler>.Instance);
    }

    [Fact]
    public async Task Reconcile_CreatesWebhook_WhenNotExists()
    {
        var webhookId = ResourceIdentity.Create("audit-webhook", "default");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");

        var webhook = new WebhookDeclaration(
            Identity: webhookId,
            ServerReference: serverId,
            Name: "audit-events",
            Url: "https://events.example.com/audit",
            EventFilters: ["object.created", "object.deleted"],
            ResourceFilters: ["my-bucket/*"],
            Active: true);

        var outcome = await reconciler.Reconcile(webhook);

        Assert.True(outcome.IsSuccessful);
        Assert.Single(vesselFactory.Port.Webhooks);
        var created = vesselFactory.Port.Webhooks[0];
        Assert.Equal("audit-events", created.Name);
        Assert.Equal("https://events.example.com/audit", created.Url);
        Assert.Equal(["object.created", "object.deleted"], created.EventFilters);
        Assert.Equal(["my-bucket/*"], created.ResourceFilters);
        Assert.True(created.Active);
        Assert.True(vesselFactory.Port.Disposed);

        Assert.True(k8s.WebhookStatuses.TryGetValue(webhookId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal(created.Id, status.WebhookId);
        Assert.NotNull(status.Conditions);
        Assert.Equal(ConditionTypes.StatusTrue, status.Conditions[0].Status);
    }

    [Fact]
    public async Task Reconcile_ResolvesSecretFromSecretRef()
    {
        var webhookId = ResourceIdentity.Create("secure-webhook", "default");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");

        k8s.GenericSecrets[("default", "webhook-signing-key", "secret")] = "super-secret-token";

        var webhook = new WebhookDeclaration(
            Identity: webhookId,
            ServerReference: serverId,
            Name: "secure-events",
            Url: "https://events.example.com/secure",
            SecretRef: new WebhookSecretRef("default", "webhook-signing-key", "secret"),
            EventFilters: ["*"],
            Active: true);

        var outcome = await reconciler.Reconcile(webhook);

        Assert.True(outcome.IsSuccessful);
        Assert.Single(vesselFactory.Port.Webhooks);
        var created = vesselFactory.Port.Webhooks[0];
        Assert.Equal("super-secret-token", created.Secret);

        Assert.True(k8s.WebhookStatuses.TryGetValue(webhookId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
    }

    [Fact]
    public async Task Reconcile_UpdatesWebhook_WhenPropertiesChange()
    {
        var webhookId = ResourceIdentity.Create("update-webhook", "default");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");

        // Seed existing webhook
        var existing = new WebhookDto(
            Id: "existing-wh-1",
            Name: "stream-events",
            Url: "https://events.example.com/old",
            Secret: null,
            EventFilters: ["*"],
            ResourceFilters: null,
            Active: true,
            CreatedAt: DateTimeOffset.UtcNow,
            LastTriggeredAt: null,
            LastStatusCode: null,
            LastError: null,
            IsStatic: false);
        vesselFactory.Port.Webhooks.Add(existing);

        var webhook = new WebhookDeclaration(
            Identity: webhookId,
            ServerReference: serverId,
            Name: "stream-events",
            Url: "https://events.example.com/new",
            EventFilters: ["object.created"],
            Active: true);

        var outcome = await reconciler.Reconcile(webhook);

        Assert.True(outcome.IsSuccessful);
        Assert.Single(vesselFactory.Port.Webhooks);
        var updated = vesselFactory.Port.Webhooks[0];
        Assert.Equal("https://events.example.com/new", updated.Url);
        Assert.Equal(["object.created"], updated.EventFilters);

        Assert.True(k8s.WebhookStatuses.TryGetValue(webhookId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal("existing-wh-1", status.WebhookId);
    }

    [Fact]
    public async Task Reconcile_SecretRefNotFound_SetsErrorStatus()
    {
        var webhookId = ResourceIdentity.Create("missing-secret-webhook", "default");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");

        var webhook = new WebhookDeclaration(
            Identity: webhookId,
            ServerReference: serverId,
            Name: "missing-secret-events",
            Url: "https://events.example.com",
            SecretRef: new WebhookSecretRef("default", "non-existent-secret", "secret"));

        var outcome = await reconciler.Reconcile(webhook);

        Assert.False(outcome.IsSuccessful);
        Assert.Empty(vesselFactory.Port.Webhooks);

        Assert.True(k8s.WebhookStatuses.TryGetValue(webhookId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
        Assert.NotNull(status.ErrorMessage);
    }

    [Fact]
    public async Task Reconcile_VesselError_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailEnsureWebhook = true;
        var webhookId = ResourceIdentity.Create("fail-webhook", "default");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");

        var webhook = new WebhookDeclaration(
            Identity: webhookId,
            ServerReference: serverId,
            Name: "fail-events",
            Url: "https://events.example.com");

        var outcome = await reconciler.Reconcile(webhook);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.WebhookStatuses.TryGetValue(webhookId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
