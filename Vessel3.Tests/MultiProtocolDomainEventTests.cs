using System.Collections.Concurrent;
using Vessel3.Primitives;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class MultiProtocolDomainEventTests : IDisposable
{
    private readonly string testDir;

    public MultiProtocolDomainEventTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-events-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class TestEventPublisher : IWebhookEventPublisher
    {
        public ConcurrentQueue<VesselEvent> Events { get; } = new();
        public void Publish(VesselEvent @event) => Events.Enqueue(@event);
    }

    [Fact]
    public async Task ObjectStore_PutAndCopyAndDelete_EmitsCloudEvents()
    {
        var publisher = new TestEventPublisher();
        var fileSync = OperatingSystem.IsLinux() ? new PosixFileSync() : (IFileSync)new PortableFileSync();
        var durable = new DurableWrite(fileSync);
        var regOpts = new BucketRegistryOptions(testDir);
        using var registry = new BucketRegistry(regOpts, fileSync, durable, publisher);
        Assert.IsType<Result<bool>.Success>(registry.Create("my-bucket"));

        var blobOpts = new BlobPoolOptions(Path.Combine(testDir, "blobs"));
        var blobs = new BlobPool(blobOpts, fileSync);
        var pre = new PreconditionEvaluator();
        var gate = new GcGate();
        var store = new ObjectStore(registry, blobs, pre, gate, publisher);

        var payload = "hello world"u8.ToArray();
        var putReq = new ObjectPutRequest(
            "my-bucket",
            "docs/hello.txt",
            new MemoryStream(payload),
            payload.Length,
            "text/plain",
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: new Dictionary<string, string>(),
            Tags: new Dictionary<string, string>(),
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: CancellationToken.None,
            Protocol: "s3",
            Actor: "alice",
            Host: "s3.local:9000");

        var putRes = await store.Put(putReq);
        Assert.True(putRes.TryGetValue(out var outcome, out _));

        var events = publisher.Events.ToArray();
        var putEvent = events.FirstOrDefault(e => e.Type == VesselEventTypes.ObjectCreated);
        Assert.NotNull(putEvent);
        Assert.Equal("1.0", putEvent.SpecVersion);
        Assert.Equal("/vessel3", putEvent.Source);
        Assert.Equal("my-bucket/docs/hello.txt", putEvent.Subject);
        Assert.Equal("alice", putEvent.Actor);
        Assert.Equal("s3.local:9000", putEvent.Host);
        Assert.Equal("application/json", putEvent.DataContentType);
        Assert.NotNull(putEvent.Data);
        Assert.Equal("s3", putEvent.Data["protocol"]);
        Assert.Equal(payload.Length.ToString(), putEvent.Data["size"]);
        Assert.Equal(outcome.Sha256, putEvent.Data["blobSha"]);

        var azurePutReq = new ObjectPutRequest(
            "my-bucket",
            "azure/blob.bin",
            new MemoryStream(payload),
            payload.Length,
            "application/octet-stream",
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: new Dictionary<string, string>(),
            Tags: new Dictionary<string, string>(),
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: CancellationToken.None,
            Protocol: "azure",
            Actor: "bob");

        var azureRes = await store.Put(azurePutReq);
        Assert.True(azureRes.TryGetValue(out _, out _));

        var azureEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Subject == "my-bucket/azure/blob.bin");
        Assert.NotNull(azureEvent);
        Assert.Equal(VesselEventTypes.ObjectCreated, azureEvent.Type);
        Assert.Equal("azure", azureEvent.Data!["protocol"]);
        Assert.Equal("bob", azureEvent.Actor);

        var delRes = store.Delete("my-bucket", "docs/hello.txt");
        Assert.True(delRes.TryGetValue(out _, out _));

        var delEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Type == VesselEventTypes.ObjectDeleted);
        Assert.NotNull(delEvent);
        Assert.Equal("1.0", delEvent.SpecVersion);
        Assert.Equal("my-bucket/docs/hello.txt", delEvent.Subject);
        Assert.Equal("s3", delEvent.Data!["protocol"]);
    }

    [Fact]
    public void BucketRegistry_CreateAndDelete_EmitsCloudEvents()
    {
        var publisher = new TestEventPublisher();
        var fileSync = OperatingSystem.IsLinux() ? new PosixFileSync() : (IFileSync)new PortableFileSync();
        var durable = new DurableWrite(fileSync);
        var regOpts = new BucketRegistryOptions(testDir);
        using var registry = new BucketRegistry(regOpts, fileSync, durable, publisher);

        var createRes = Assert.IsType<Result<bool>.Success>(registry.Create("cloud-bucket", "owner-123"));
        Assert.True(createRes.Value);

        var createEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Type == VesselEventTypes.BucketCreated);
        Assert.NotNull(createEvent);
        Assert.Equal("1.0", createEvent.SpecVersion);
        Assert.Equal("cloud-bucket", createEvent.Subject);
        Assert.Equal("owner-123", createEvent.Data!["owner"]);

        var delRes = registry.Delete("cloud-bucket");
        Assert.IsType<Result.OkResult>(delRes);

        var delEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Type == VesselEventTypes.BucketDeleted);
        Assert.NotNull(delEvent);
        Assert.Equal("cloud-bucket", delEvent.Subject);
    }

    [Fact]
    public void IdentityRegistry_CreateAndDeleteUser_EmitsCloudEvents()
    {
        var publisher = new TestEventPublisher();
        var idOpts = new IdentityOptions(Path.Combine(testDir, "iam"));
        using var identity = new IdentityRegistry(idOpts, TimeProvider.System, publisher);

        var userRes = Assert.IsType<Result<User>.Success>(identity.CreateUser("devops-lead", UserRole.Admin));
        var user = userRes.Value;

        var userEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Type == VesselEventTypes.UserCreated);
        Assert.NotNull(userEvent);
        Assert.Equal("1.0", userEvent.SpecVersion);
        Assert.Equal(user.Id, userEvent.Subject);
        Assert.Equal("Admin", userEvent.Data!["role"]);

        var delRes = identity.DeleteUser(user.Id);
        Assert.IsType<Result.OkResult>(delRes);

        var delEvent = publisher.Events.ToArray().FirstOrDefault(e => e.Type == VesselEventTypes.UserDeleted);
        Assert.NotNull(delEvent);
        Assert.Equal(user.Id, delEvent.Subject);
    }

    [Theory]
    [InlineData("object.*", "object.created", true)]
    [InlineData("object.*", "object.deleted", true)]
    [InlineData("bucket.*", "bucket.created", true)]
    [InlineData("bucket.*", "bucket.deleted", true)]
    [InlineData("user.*", "user.created", true)]
    [InlineData("user.*", "user.deleted", true)]
    [InlineData("object.created", "object.created", true)]
    [InlineData("object.created", "object.deleted", false)]
    [InlineData("object.*", "container.image.pushed", false)]
    public void MatchesFilter_ValidatesDomainEventTopics(string filter, string eventType, bool expected)
    {
        var matches = WebhookDeliveryWorker.MatchesFilter([filter], eventType);
        Assert.Equal(expected, matches);
    }
}
