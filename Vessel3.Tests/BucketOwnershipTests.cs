using Xunit;

namespace Vessel3.Tests;

public class BucketOwnershipTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketOwnershipTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-bucket-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private BucketRegistry Registry()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(reg);
        return reg;
    }

    [Fact]
    public void Create_WithOwner_PersistsAndCanBeRetrieved()
    {
        var reg = Registry();
        var createResult = reg.Create("alice-bucket", "usr_alice123");
        Assert.IsType<Result<bool>.Success>(createResult);

        var ownerResult = reg.GetOwner("alice-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Equal("usr_alice123", owner);

        using var reg2 = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        var ownerResult2 = reg2.GetOwner("alice-bucket");
        Assert.True(ownerResult2.TryGetValue(out var owner2, out _));
        Assert.Equal("usr_alice123", owner2);
    }

    [Fact]
    public void Create_WithoutOwner_HasNullOwner()
    {
        var reg = Registry();
        reg.Create("orphan-bucket");

        var ownerResult = reg.GetOwner("orphan-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Null(owner);
    }

    [Fact]
    public void SetOwner_ValidOwner_UpdatesAndPersists()
    {
        var reg = Registry();
        reg.Create("team-bucket", "usr_alice");

        var updateResult = reg.SetOwner("team-bucket", "usr_bob");
        Assert.Same(Result.Ok, updateResult);

        var ownerResult = reg.GetOwner("team-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Equal("usr_bob", owner);

        using var reg2 = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        var reloadedOwner = reg2.GetOwner("team-bucket");
        Assert.True(reloadedOwner.TryGetValue(out var ownerReloaded, out _));
        Assert.Equal("usr_bob", ownerReloaded);

        var emptyOwnerResult = reg.SetOwner("team-bucket", "");
        Assert.True(emptyOwnerResult.Match(() => false, err => err is InvalidArgumentError));

        var missingBucketResult = reg.SetOwner("nonexistent", "usr_someone");
        Assert.True(missingBucketResult.Match(() => false, err => err is NoSuchBucketError));
    }

    [Fact]
    public void List_OwnerId_FiltersCorrectly()
    {
        var reg = Registry();
        reg.Create("bucket-alice-1", "usr_alice");
        reg.Create("bucket-alice-2", "usr_alice");
        reg.Create("bucket-bob-1", "usr_bob");
        reg.Create("bucket-unowned");

        var allBuckets = reg.List().ToList();
        Assert.Equal(4, allBuckets.Count);

        var aliceBuckets = reg.List("usr_alice").ToList();
        Assert.Equal(2, aliceBuckets.Count);
        Assert.All(aliceBuckets, b => Assert.Equal("usr_alice", b.OwnerId));
        Assert.Contains(aliceBuckets, b => b.Name == "bucket-alice-1");
        Assert.Contains(aliceBuckets, b => b.Name == "bucket-alice-2");

        var bobBuckets = reg.List("usr_bob").ToList();
        Assert.Single(bobBuckets);
        Assert.Equal("bucket-bob-1", bobBuckets[0].Name);
        Assert.Equal("usr_bob", bobBuckets[0].OwnerId);

        var charlieBuckets = reg.List("usr_charlie").ToList();
        Assert.Empty(charlieBuckets);
    }

    [Fact]
    public void Create_WithCallerIdentity_AssignsOwnerAndEnforcesReadOnly()
    {
        var reg = Registry();
        var alice = new CallerIdentity("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
        var admin = new CallerIdentity("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");
        var ro = new CallerIdentity("usr_ro", "ro_user", UserRole.ReadOnly, "V3AKRO00000000000000");

        var createAlice = reg.Create("alice-b", alice);
        Assert.True(createAlice.TryGetValue(out var createdAlice, out _));
        Assert.True(createdAlice);
        Assert.Equal("usr_alice", ((Result<string?>.Success)reg.GetOwner("alice-b")).Value);

        var createRo = reg.Create("ro-b", ro);
        Assert.True(createRo.Match(_ => false, err => err is AccessDeniedError));

        var createAdminExplicit = reg.Create("admin-assigned", admin, "usr_bob");
        Assert.True(createAdminExplicit.TryGetValue(out var createdAdmin, out _));
        Assert.True(createdAdmin);
        Assert.Equal("usr_bob", ((Result<string?>.Success)reg.GetOwner("admin-assigned")).Value);
    }

    [Fact]
    public void Delete_WithCallerIdentity_AllowsOwnerOrAdminDeniesOther()
    {
        var reg = Registry();
        var alice = new CallerIdentity("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
        var bob = new CallerIdentity("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");
        var admin = new CallerIdentity("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");
        var ro = new CallerIdentity("usr_ro", "ro_user", UserRole.ReadOnly, "V3AKRO00000000000000");

        reg.Create("shared-b", alice);

        var deleteByBob = reg.Delete("shared-b", bob);
        Assert.True(deleteByBob.Match(() => false, err => err is AccessDeniedError));

        var deleteByRo = reg.Delete("shared-b", ro);
        Assert.True(deleteByRo.Match(() => false, err => err is AccessDeniedError));

        var deleteByAdmin = reg.Delete("shared-b", admin);
        Assert.Same(Result.Ok, deleteByAdmin);

        reg.Create("alice-solo", alice);
        var deleteByAlice = reg.Delete("alice-solo", alice);
        Assert.Same(Result.Ok, deleteByAlice);
    }

    [Fact]
    public void List_WithCallerIdentity_FiltersForMemberReturnsAllForAdmin()
    {
        var reg = Registry();
        var alice = new CallerIdentity("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
        var bob = new CallerIdentity("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");
        var admin = new CallerIdentity("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");

        reg.Create("alice-1", alice);
        reg.Create("alice-2", alice);
        reg.Create("bob-1", bob);

        var aliceView = reg.List(alice).ToList();
        Assert.Equal(2, aliceView.Count);
        Assert.All(aliceView, b => Assert.Equal("usr_alice", b.OwnerId));

        var bobView = reg.List(bob).ToList();
        Assert.Single(bobView);
        Assert.Equal("bob-1", bobView[0].Name);

        var adminView = reg.List(admin).ToList();
        Assert.Equal(3, adminView.Count);

    }

    [Fact]
    public void Mutations_CallerIdentity_EnforcesAuthorization()
    {
        var reg = Registry();
        var alice = new CallerIdentity("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
        var bob = new CallerIdentity("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");
        var admin = new CallerIdentity("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");

        reg.Create("conf-b", alice);

        var bobVersioning = reg.SetVersioning("conf-b", VersioningStatus.Enabled, bob);
        Assert.True(bobVersioning.Match(() => false, err => err is AccessDeniedError));

        var aliceVersioning = reg.SetVersioning("conf-b", VersioningStatus.Enabled, alice);
        Assert.Same(Result.Ok, aliceVersioning);

        var bobAccess = reg.SetAccess("conf-b", new BucketAccess(true, false), bob);
        Assert.True(bobAccess.Match(() => false, err => err is AccessDeniedError));

        var adminAccess = reg.SetAccess("conf-b", new BucketAccess(true, false), admin);
        Assert.Same(Result.Ok, adminAccess);
    }

    [Fact]
    public void AuthorizeAccess_Enforces_Capabilities()
    {
        var reg = Registry();
        var alice = new CallerIdentity("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
        var bob = new CallerIdentity("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");

        reg.Create("auth-b", alice);

        Assert.Same(Result.Ok, reg.AuthorizeAccess("auth-b", alice, BucketCapability.Read));
        Assert.Same(Result.Ok, reg.AuthorizeAccess("auth-b", alice, BucketCapability.Write));
        Assert.Same(Result.Ok, reg.AuthorizeAccess("auth-b", alice, BucketCapability.Admin));

        Assert.True(reg.AuthorizeAccess("auth-b", bob, BucketCapability.Read).Match(() => false, err => err is AccessDeniedError));
        Assert.True(reg.AuthorizeAccess("auth-b", bob, BucketCapability.Write).Match(() => false, err => err is AccessDeniedError));
        Assert.True(reg.AuthorizeAccess("auth-b", bob, BucketCapability.Admin).Match(() => false, err => err is AccessDeniedError));

        reg.SetAccess("auth-b", new BucketAccess(PublicRead: true, ReadOnly: false));
        Assert.Same(Result.Ok, reg.AuthorizeAccess("auth-b", bob, BucketCapability.Read));
        Assert.Same(Result.Ok, reg.AuthorizeAccess("auth-b", null, BucketCapability.Read));
        Assert.True(reg.AuthorizeAccess("auth-b", bob, BucketCapability.Write).Match(() => false, err => err is AccessDeniedError));
    }
}

