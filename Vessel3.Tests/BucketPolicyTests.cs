using Xunit;

namespace Vessel3.Tests;

public class BucketPolicyTests
{
    private static readonly CallerIdentity Admin = new("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");
    private static readonly CallerIdentity Alice = new("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
    private static readonly CallerIdentity Bob = new("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");
    private static readonly CallerIdentity ReadOnlyUser = new("usr_ro", "ro_user", UserRole.ReadOnly, "V3AKRO00000000000000");

    [Fact]
    public void Authorize_PublicRead_GrantsReadToAnyoneIncludingAnonymous()
    {
        var publicAccess = new BucketAccess(PublicRead: true, ReadOnly: false);

        Assert.True(BucketPolicy.Allows(null, "usr_alice", publicAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Bob, "usr_alice", publicAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(ReadOnlyUser, "usr_alice", publicAccess, BucketCapability.Read));

        Assert.False(BucketPolicy.Allows(null, "usr_alice", publicAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(null, "usr_alice", publicAccess, BucketCapability.Admin));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", publicAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", publicAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_Anonymous_DeniedWhenNotPublic()
    {
        var privateAccess = BucketAccess.Private;

        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_Admin_HasWildcardAccess()
    {
        var privateAccess = BucketAccess.Private;

        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", privateAccess, BucketCapability.Admin));

        Assert.True(BucketPolicy.Allows(Admin, null, privateAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Admin, null, privateAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Admin, null, privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_Owner_HasReadWriteAdminOnOwnedBucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_NonOwnerMember_DeniedOnPrivateBucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_ReadOnlyUser_DeniesWriteAndAdminEvenOnOwnedBucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.True(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_ReadOnlyBucket_BlocksWritesForAllCallers()
    {
        var readOnlyAccess = new BucketAccess(PublicRead: false, ReadOnly: true);

        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Admin));

        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Admin));

        var authRes = BucketPolicy.Authorize(Admin, "usr_alice", readOnlyAccess, "test-bucket", BucketCapability.Write);
        Assert.True(authRes is Result.Failure);
        Assert.IsType<BucketIsReadOnlyError>(((Result.Failure)authRes).Error);
    }

    [Fact]
    public void Authorize_Result_ReturnsOkOrAccessDenied()
    {
        var privateAccess = BucketAccess.Private;

        var ok = BucketPolicy.Authorize(Alice, "usr_alice", privateAccess, "test-bucket", BucketCapability.Read);
        Assert.Same(Result.Ok, ok);

        var denied = BucketPolicy.Authorize(Bob, "usr_alice", privateAccess, "test-bucket", BucketCapability.Write);
        Assert.True(denied.Match(() => false, err => err is AccessDeniedError));
    }
}
