using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Domain;
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
        var server = new VesselServerCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "test-store", Namespace = "storage" },
            Spec = new VesselServerSpec
            {
                Replicas = 1,
                Service = new VesselServerServiceSpec { Port = 9000 },
                Auth = new VesselServerAuthSpec { AdminSecretName = "test-store-admin-creds" }
            }
        };

        var outcome = await reconciler.Reconcile(server);

        Assert.True(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(("storage", "test-store"), out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal("http://test-store.storage.svc:9000", status.Endpoint);
        Assert.Equal("test-store-admin-creds", status.AdminSecret);
        Assert.Equal(1, status.ReadyReplicas);
    }

    [Fact]
    public async Task Reconcile_SecretCreationFailure_SetsErrorStatus()
    {
        k8s.ShouldFailSecretCreation = true;
        var server = new VesselServerCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-store", Namespace = "default" },
            Spec = new VesselServerSpec()
        };

        var outcome = await reconciler.Reconcile(server);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(("default", "failing-store"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_WorkloadReconciliationFailure_SetsErrorStatus()
    {
        k8s.ShouldFailWorkloadReconciliation = true;
        var server = new VesselServerCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-workload", Namespace = "default" },
            Spec = new VesselServerSpec()
        };

        var outcome = await reconciler.Reconcile(server);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.ServerStatuses.TryGetValue(("default", "failing-workload"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
