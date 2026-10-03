using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Domain;
using Xunit;

namespace Vessel3.Tests.Operator;

public sealed class UserReconcilerTests
{
    private readonly InMemoryVesselPortFactory vesselFactory = new();
    private readonly InMemoryKubernetesPort k8s = new();
    private readonly UserReconciler reconciler;

    public UserReconcilerTests()
    {
        reconciler = new UserReconciler(vesselFactory, k8s, NullLogger<UserReconciler>.Instance);
    }

    [Fact]
    public async Task Reconcile_ValidUser_EnsuresUserAndWritesSecret()
    {
        var userCr = new VesselUserCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "app-iam", Namespace = "apps" },
            Spec = new VesselUserSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "storage" },
                Username = "backup-agent",
                Role = "Admin",
                WriteSecret = new UserSecretSpec
                {
                    SecretName = "backup-agent-s3-creds",
                    SecretNamespace = "apps",
                    Keys = new SecretKeyMapping
                    {
                        AccessKey = "AWS_ACCESS_KEY_ID",
                        SecretKey = "AWS_SECRET_ACCESS_KEY",
                        Endpoint = "AWS_ENDPOINT_URL_S3",
                        Region = "AWS_DEFAULT_REGION"
                    }
                }
            }
        };

        var outcome = await reconciler.Reconcile(userCr);

        Assert.True(outcome.IsSuccessful);
        Assert.Equal("Admin", vesselFactory.Port.Users["backup-agent"]);
        Assert.Single(vesselFactory.Port.IssuedKeys);
        Assert.True(vesselFactory.Port.Disposed);

        Assert.True(k8s.UserSecrets.TryGetValue(("apps", "backup-agent-s3-creds"), out var secretData));
        Assert.Equal("test-key-id", secretData["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("test-secret-key", secretData["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("http://vessel-store.storage.svc:9000", secretData["AWS_ENDPOINT_URL_S3"]);
        Assert.Equal("us-east-1", secretData["AWS_DEFAULT_REGION"]);

        Assert.True(k8s.UserStatuses.TryGetValue(("apps", "app-iam"), out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal("backup-agent-s3-creds", status.SecretRef);
    }

    [Fact]
    public async Task Reconcile_ConnectionFailure_SetsErrorStatus()
    {
        vesselFactory.ShouldFailConnection = true;
        var userCr = new VesselUserCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failed-user", Namespace = "apps" },
            Spec = new VesselUserSpec
            {
                ServerRef = new ServerReference { Name = "missing-store", Namespace = "storage" },
                Username = "test-user"
            }
        };

        var outcome = await reconciler.Reconcile(userCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(("apps", "failed-user"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_EnsureUserFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailEnsureUser = true;
        var userCr = new VesselUserCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-user", Namespace = "apps" },
            Spec = new VesselUserSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "storage" },
                Username = "invalid-user"
            }
        };

        var outcome = await reconciler.Reconcile(userCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(("apps", "failing-user"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_IssueKeyFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailIssueKey = true;
        var userCr = new VesselUserCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-key-user", Namespace = "apps" },
            Spec = new VesselUserSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "storage" },
                Username = "test-user"
            }
        };

        var outcome = await reconciler.Reconcile(userCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(("apps", "failing-key-user"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_SecretWriteFailure_SetsErrorStatus()
    {
        k8s.ShouldFailUserSecretWrite = true;
        var userCr = new VesselUserCustomResource
        {
            Metadata = new CustomResourceMetadata { Name = "failing-secret-user", Namespace = "apps" },
            Spec = new VesselUserSpec
            {
                ServerRef = new ServerReference { Name = "vessel-store", Namespace = "storage" },
                Username = "test-user"
            }
        };

        var outcome = await reconciler.Reconcile(userCr);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(("apps", "failing-secret-user"), out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
