using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Domain;
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
        var bucketCr = new VesselBucketCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "app-assets", Namespace = "production" },
            Spec = new VesselBucketSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "production" },
                BucketName = "assets",
                Versioning = "Enabled",
                Website = new BucketWebsiteSpec { IndexDocument = "index.html", ErrorDocument = "404.html" }
            }
        };

        var outcome = await reconciler.Reconcile(bucketCr);

        Assert.True(outcome.IsSuccessful);
        Assert.Contains("assets", vesselFactory.Port.Buckets);
        Assert.Equal("Enabled", vesselFactory.Port.BucketVersioning["assets"]);
        Assert.Equal("index.html", vesselFactory.Port.BucketWebsites["assets"].IndexDocument);
        Assert.True(vesselFactory.Port.Disposed);

        Assert.True(k8s.BucketStatuses.TryGetValue(("production", "app-assets"), out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal(1024L, status.SizeBytes);
        Assert.Equal(5L, status.ObjectCount);
    }

    [Fact]
    public async Task Reconcile_ConnectionFailure_SetsErrorStatus()
    {
        vesselFactory.ShouldFailConnection = true;
        var bucketCr = new VesselBucketCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failed-bucket", Namespace = "production" },
            Spec = new VesselBucketSpec
            {
                ServerRef = new ServerReference { Name = "missing-store", Namespace = "production" },
                BucketName = "test-bucket"
            }
        };

        var outcome = await reconciler.Reconcile(bucketCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.BucketStatuses.TryGetValue(("production", "failed-bucket"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_EnsureBucketFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailEnsureBucket = true;
        var bucketCr = new VesselBucketCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-bucket", Namespace = "production" },
            Spec = new VesselBucketSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "production" },
                BucketName = "corrupt-bucket"
            }
        };

        var outcome = await reconciler.Reconcile(bucketCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.BucketStatuses.TryGetValue(("production", "failing-bucket"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
