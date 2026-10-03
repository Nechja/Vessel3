using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Domain.Models;
using Xunit;

namespace Vessel3.Tests.Operator;

public sealed class BucketReconcilerTests
{
    private readonly InMemoryVesselPortFactory vesselFactory = new();
    private readonly InMemoryKubernetesPort k8s = new();
    private readonly BucketReconciler reconciler;

    public BucketReconcilerTests()
    {
        reconciler = new BucketReconciler(vesselFactory, k8s, NullLogger<BucketReconciler>.Instance);
    }

    [Fact]
    public async Task Reconcile_ValidBucket_EnsuresBucketAndUpdatesStats()
    {
        var bucketId = ResourceIdentity.Create("app-assets", "production");
        var serverId = ResourceIdentity.Create("vessel-store", "production");
        var bucket = new BucketDeclaration(
            Identity: bucketId,
            ServerReference: serverId,
            BucketName: "assets",
            Versioning: "Enabled",
            Website: new BucketWebsiteDefinition("index.html", "404.html"));

        var outcome = await reconciler.Reconcile(bucket);

        Assert.True(outcome.IsSuccessful);
        Assert.Contains("assets", vesselFactory.Port.Buckets);
        Assert.Equal("Enabled", vesselFactory.Port.BucketVersioning["assets"]);
        Assert.Equal("index.html", vesselFactory.Port.BucketWebsites["assets"].IndexDocument);
        Assert.True(vesselFactory.Port.Disposed);

        Assert.True(k8s.BucketStatuses.TryGetValue(bucketId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal(1024L, status.SizeBytes);
        Assert.Equal(5L, status.ObjectCount);
    }

    [Fact]
    public async Task Reconcile_ConnectionFailure_SetsErrorStatus()
    {
        vesselFactory.ShouldFailConnection = true;
        var bucketId = ResourceIdentity.Create("failed-bucket", "production");
        var bucket = new BucketDeclaration(
            Identity: bucketId,
            ServerReference: ResourceIdentity.Create("missing-store", "production"),
            BucketName: "test-bucket");

        var outcome = await reconciler.Reconcile(bucket);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.BucketStatuses.TryGetValue(bucketId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_EnsureBucketFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailEnsureBucket = true;
        var bucketId = ResourceIdentity.Create("failing-bucket", "production");
        var bucket = new BucketDeclaration(
            Identity: bucketId,
            ServerReference: ResourceIdentity.Create("vessel-store", "production"),
            BucketName: "corrupt-bucket");

        var outcome = await reconciler.Reconcile(bucket);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.BucketStatuses.TryGetValue(bucketId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
