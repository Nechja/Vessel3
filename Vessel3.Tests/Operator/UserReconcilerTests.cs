using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Domain.Models;
using Vessel3.Primitives;
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
        var userId = ResourceIdentity.Create("app-iam", "apps");
        var serverId = ResourceIdentity.Create("vessel-store", "storage");
        var secretOutput = new SecretOutputDefinition(
            Namespace: "apps",
            Name: "backup-agent-s3-creds",
            AccessKeyField: "AWS_ACCESS_KEY_ID",
            SecretKeyField: "AWS_SECRET_ACCESS_KEY",
            EndpointField: "AWS_ENDPOINT_URL_S3",
            RegionField: "AWS_DEFAULT_REGION");

        var user = new UserDeclaration(
            Identity: userId,
            ServerReference: serverId,
            Username: "backup-agent",
            Role: "Admin",
            SecretOutput: secretOutput);

        var outcome = await reconciler.Reconcile(user);

        Assert.True(outcome.IsSuccessful);
        Assert.Equal("Admin", vesselFactory.Port.Users["backup-agent"]);
        Assert.Single(vesselFactory.Port.IssuedKeys);
        Assert.True(vesselFactory.Port.Disposed);

        Assert.True(k8s.UserSecrets.TryGetValue(("apps", "backup-agent-s3-creds"), out var secretData));
        Assert.Equal("test-key-id", secretData["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("test-secret-key", secretData["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("http://vessel-store.storage.svc:9000", secretData["AWS_ENDPOINT_URL_S3"]);
        Assert.Equal(ServerRegion.Default, secretData["AWS_DEFAULT_REGION"]);

        Assert.True(k8s.UserStatuses.TryGetValue(userId, out var status));
        Assert.Equal(PhaseNames.Ready, status.Phase);
        Assert.Equal("backup-agent-s3-creds", status.SecretRef);
    }

    [Fact]
    public async Task Reconcile_ConnectionFailure_SetsErrorStatus()
    {
        vesselFactory.ShouldFailConnection = true;
        var userId = ResourceIdentity.Create("failed-user", "apps");
        var user = new UserDeclaration(
            Identity: userId,
            ServerReference: ResourceIdentity.Create("missing-store", "storage"),
            Username: "test-user");

        var outcome = await reconciler.Reconcile(user);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(userId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_EnsureUserFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailEnsureUser = true;
        var userId = ResourceIdentity.Create("failing-user", "apps");
        var user = new UserDeclaration(
            Identity: userId,
            ServerReference: ResourceIdentity.Create("vessel-store", "storage"),
            Username: "invalid-user");

        var outcome = await reconciler.Reconcile(user);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(userId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_IssueKeyFailure_SetsErrorStatus()
    {
        vesselFactory.Port.ShouldFailIssueKey = true;
        var userId = ResourceIdentity.Create("failing-key-user", "apps");
        var user = new UserDeclaration(
            Identity: userId,
            ServerReference: ResourceIdentity.Create("vessel-store", "storage"),
            Username: "test-user");

        var outcome = await reconciler.Reconcile(user);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(userId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }

    [Fact]
    public async Task Reconcile_SecretWriteFailure_SetsErrorStatus()
    {
        k8s.ShouldFailUserSecretWrite = true;
        var userId = ResourceIdentity.Create("failing-secret-user", "apps");
        var user = new UserDeclaration(
            Identity: userId,
            ServerReference: ResourceIdentity.Create("vessel-store", "storage"),
            Username: "test-user",
            SecretOutput: new SecretOutputDefinition("apps", "failing-secret"));

        var outcome = await reconciler.Reconcile(user);

        Assert.False(outcome.IsSuccessful);
        Assert.True(k8s.UserStatuses.TryGetValue(userId, out var status));
        Assert.Equal(PhaseNames.Error, status.Phase);
    }
}
