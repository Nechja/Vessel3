using Vessel3.Primitives;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketPolicyTests
{
    private static readonly CallerIdentity Admin = new("usr_admin", "admin", UserRole.Admin, "V3AKADMIN000000000000");
    private static readonly CallerIdentity Alice = new("usr_alice", "alice", UserRole.Member, "V3AKALICE00000000000");
    private static readonly CallerIdentity Bob = new("usr_bob", "bob", UserRole.Member, "V3AKBOB0000000000000");
    private static readonly CallerIdentity ReadOnlyUser = new("usr_ro", "ro_user", UserRole.ReadOnly, "V3AKRO00000000000000");

    [Fact]
    public void PublicRead_Grants_Read_To_Anyone_Including_Anonymous()
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
    public void Anonymous_Denied_When_Not_Public()
    {
        var privateAccess = BucketAccess.Private;

        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(null, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Admin_Has_Wildcard_Access()
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
    public void Owner_Has_Read_Write_Admin_On_Owned_Bucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Non_Owner_Member_Denied_All_On_Private_Bucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(Bob, "usr_alice", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void ReadOnly_User_Denied_Write_And_Admin_Even_On_Owned_Bucket()
    {
        var privateAccess = BucketAccess.Private;

        Assert.True(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Write));
        Assert.False(BucketPolicy.Allows(ReadOnlyUser, "usr_ro", privateAccess, BucketCapability.Admin));
    }

    [Fact]
    public void ReadOnly_Bucket_Blocks_Writes_For_Owner_And_Members_Except_Admin()
    {
        var readOnlyAccess = new BucketAccess(PublicRead: false, ReadOnly: true);

        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Read));
        Assert.False(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Alice, "usr_alice", readOnlyAccess, BucketCapability.Admin));

        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Read));
        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Write));
        Assert.True(BucketPolicy.Allows(Admin, "usr_alice", readOnlyAccess, BucketCapability.Admin));
    }

    [Fact]
    public void Authorize_Returns_Ok_Or_AccessDeniedError()
    {
        var privateAccess = BucketAccess.Private;

        var ok = BucketPolicy.Authorize(Alice, "usr_alice", privateAccess, "test-bucket", BucketCapability.Read);
        Assert.Same(Result.Ok, ok);

        var denied = BucketPolicy.Authorize(Bob, "usr_alice", privateAccess, "test-bucket", BucketCapability.Write);
        Assert.True(denied.Match(() => false, err => err is AccessDeniedError));
    }
}
