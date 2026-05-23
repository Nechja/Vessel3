using System;
using System.IO;
using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class ObjectLockTests : IDisposable
{
    private readonly string root;
    private readonly BucketRegistry registry;

    public ObjectLockTests()
    {
        root = Path.Combine(Path.GetTempPath(), "vessel3-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        registry = new BucketRegistry(new BucketRegistryOptions(root));
    }

    public void Dispose()
    {
        registry.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    private static PutRequest MakePut(string body = "data") => new(
        BlobSha: "deadbeef",
        Md5: "feedface",
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void ObjectLock_requires_versioning_enabled()
    {
        const string b = "lock-req-ver";
        Assert.IsType<Result<bool>.Success>(registry.Create(b));

        // Bucket has no versioning; enabling object lock must fail.
        var setResult = registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));
        var failure = Assert.IsType<Result<bool>.Failure>(setResult);
        Assert.IsType<InvalidBucketStateError>(failure.Error);
        Assert.Equal(409, failure.Error.Status);

        // Enable versioning and retry — should succeed.
        Assert.IsType<Result<bool>.Success>(registry.SetVersioning(b, VersioningStatus.Enabled));
        Assert.IsType<Result<bool>.Success>(registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null)));
    }

    [Fact]
    public void ObjectLock_cannot_be_disabled_once_enabled()
    {
        const string b = "lock-no-disable";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));

        var disabled = registry.SetObjectLock(b, new ObjectLockConfig(Enabled: false, Default: null));
        var failure = Assert.IsType<Result<bool>.Failure>(disabled);
        Assert.IsType<InvalidBucketStateError>(failure.Error);
    }

    [Fact]
    public void Suspending_versioning_blocked_on_object_lock_bucket()
    {
        const string b = "lock-no-suspend";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));

        var sus = registry.SetVersioning(b, VersioningStatus.Suspended);
        Assert.IsType<InvalidBucketStateError>(Assert.IsType<Result<bool>.Failure>(sus).Error);
    }

    [Fact]
    public void Compliance_retention_cannot_be_lowered_or_removed()
    {
        const string b = "compliance";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        var v = put.Value.VersionId;

        var until = DateTimeOffset.UtcNow.AddDays(30);
        Assert.IsType<Result<bool>.Success>(registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Compliance, until), bypassGovernance: false));

        // Lowering by date is rejected — even with the bypass header.
        var lowered = registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Compliance, until.AddDays(-1)), bypassGovernance: true);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<bool>.Failure>(lowered).Error);

        // Downgrading to GOVERNANCE is rejected too.
        var downgraded = registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Governance, until.AddDays(1)), bypassGovernance: true);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<bool>.Failure>(downgraded).Error);

        // Extending is fine.
        Assert.IsType<Result<bool>.Success>(registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Compliance, until.AddDays(7)), bypassGovernance: false));
    }

    [Fact]
    public void Governance_retention_lowers_only_with_bypass()
    {
        const string b = "governance";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        var v = put.Value.VersionId;
        var until = DateTimeOffset.UtcNow.AddDays(30);
        registry.PutRetention(b, "k", v, new Retention(RetentionMode.Governance, until), bypassGovernance: false);

        // Lowering without bypass — denied.
        var noBypass = registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Governance, until.AddDays(-5)), bypassGovernance: false);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<bool>.Failure>(noBypass).Error);

        // Same operation WITH bypass — allowed.
        Assert.IsType<Result<bool>.Success>(registry.PutRetention(b, "k", v,
            new Retention(RetentionMode.Governance, until.AddDays(-5)), bypassGovernance: true));

        // Delete without bypass blocked while retention is active.
        var del = registry.HardDeleteVersion(b, "k", v, bypassGovernance: false);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<DeleteOutcome>.Failure>(del).Error);

        // Delete with bypass succeeds.
        var delBypass = registry.HardDeleteVersion(b, "k", v, bypassGovernance: true);
        Assert.IsType<Result<DeleteOutcome>.Success>(delBypass);
    }

    [Fact]
    public void Legal_hold_blocks_delete_until_removed()
    {
        const string b = "hold";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));

        var v = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut())).Value.VersionId;
        Assert.IsType<Result<bool>.Success>(registry.PutLegalHold(b, "k", v, on: true));

        // Bypass header does NOT defeat legal hold.
        var blocked = registry.HardDeleteVersion(b, "k", v, bypassGovernance: true);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<DeleteOutcome>.Failure>(blocked).Error);

        // Remove the hold; delete now succeeds.
        Assert.IsType<Result<bool>.Success>(registry.PutLegalHold(b, "k", v, on: false));
        Assert.IsType<Result<DeleteOutcome>.Success>(registry.HardDeleteVersion(b, "k", v, bypassGovernance: false));
    }

    [Fact]
    public void Default_retention_applies_on_PUT()
    {
        const string b = "default-ret";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(
            Enabled: true,
            Default: new ObjectLockDefault(RetentionMode.Governance, Days: 7, Years: null)));

        var now = DateTimeOffset.UtcNow;
        var initial = new Retention(RetentionMode.Governance, now.AddDays(7));
        var put = Assert.IsType<Result<PutEntry>.Success>(
            registry.AppendPut(b, "k", MakePut() with { Retention = initial })).Value;

        Assert.NotNull(put.Retention);
        Assert.Equal(RetentionMode.Governance, put.Retention!.Mode);
        Assert.True(put.Retention.RetainUntilDate > now);

        // Delete blocked.
        var del = registry.HardDeleteVersion(b, "k", put.VersionId, bypassGovernance: false);
        Assert.IsType<AccessDeniedError>(Assert.IsType<Result<DeleteOutcome>.Failure>(del).Error);
    }
}
