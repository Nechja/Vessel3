using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Domain.Models;
using Xunit;

namespace Vessel3.Tests.Operator;

public sealed class ServerReconcilerTests
{
    private readonly InMemoryKubernetesPort k8s = new();
    private readonly ServerReconciler reconciler;

    public ServerReconcilerTests()
    {
        reconciler = new ServerReconciler(k8s, NullLogger<ServerReconciler>.Instance);
    }

    [Fact]
    public async Task Reconcile_ValidServer_SetsReadyStatusAndProvisionsWorkload()
    {
        var serverId = ResourceIdentity.Create("test-store", "storage");
        var server = new ServerDeclaration(
            Identity: serverId,
            Replicas: 1,
            Port: 9000,
            AdminSecretName: "test-store-admin-creds");

        var outcome = await reconciler.Reconcile(server);

        Assert.True(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(serverId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal("http://test-store.storage.svc:9000", status.Endpoint);
        Assert.Equal("test-store-admin-creds", status.AdminSecret);
        Assert.Equal(1, status.ReadyReplicas);
    }

    [Fact]
    public async Task Reconcile_SecretCreationFailure_SetsErrorStatus()
    {
        k8s.ShouldFailSecretCreation = true;
        var serverId = ResourceIdentity.Create("failing-store", "default");
        var server = new ServerDeclaration(serverId);

        var outcome = await reconciler.Reconcile(server);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(serverId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_WorkloadReconciliationFailure_SetsErrorStatus()
    {
        k8s.ShouldFailWorkloadReconciliation = true;
        var serverId = ResourceIdentity.Create("failing-workload", "default");
        var server = new ServerDeclaration(serverId);

        var outcome = await reconciler.Reconcile(server);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(serverId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
