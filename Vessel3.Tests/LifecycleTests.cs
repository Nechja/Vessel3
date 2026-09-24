using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Vessel3.Server;
using Vessel3.Server.Lifecycle;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class LifecycleTests : IDisposable
{
    private readonly string root;
    private readonly BucketRegistry registry;
    private readonly LifecycleSweeper sweeper;

    public LifecycleTests()
    {
        root = Path.Combine(Path.GetTempPath(), "vessel3-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        registry = new BucketRegistry(new BucketRegistryOptions(root), new PortableFileSync(), new DurableWrite(new PortableFileSync()));
        sweeper = new LifecycleSweeper(registry);
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

    private static LifecycleConfig OneRule(string prefix, int? days, bool expiredMarker = false, bool enabled = true) =>
        new(new[] { new LifecycleRule("rule-1", enabled, prefix, days, expiredMarker) });

    [Fact]
    public void Expires_current_version_on_versioned_bucket_as_delete_marker()
    {
        const string b = "lc-versioned";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetLifecycle(b, OneRule(prefix: "logs/", days: 7));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "logs/a.txt", MakePut()));
        var keepPut = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "keep/a.txt", MakePut()));

        var now = put.Value.At + TimeSpan.FromDays(8);
        var report = sweeper.Run(now);

        Assert.Equal(1, report.Expired);
        Assert.Equal(0, report.MarkersReaped);

        var kind = registry.GetCurrentKind(b, "logs/a.txt");
        Assert.Equal(VersionKind.DeleteMarker, kind);

        Assert.Equal(VersionKind.Put, registry.GetCurrentKind(b, "keep/a.txt"));
    }

    [Fact]
    public void Expires_current_version_on_unversioned_bucket_as_hard_delete()
    {
        const string b = "lc-unversioned";
        registry.Create(b);
        registry.SetLifecycle(b, OneRule(prefix: "", days: 1));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        var report = sweeper.Run(put.Value.At + TimeSpan.FromDays(2));

        Assert.Equal(1, report.Expired);
        Assert.Null(registry.GetCurrentKind(b, "k"));
    }

    [Fact]
    public void Skips_versions_with_active_retention()
    {
        const string b = "lc-locked";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));
        registry.SetLifecycle(b, OneRule(prefix: "", days: 1));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        registry.PutRetention(b, "k", put.Value.VersionId,
            new Retention(RetentionMode.Compliance, DateTimeOffset.UtcNow.AddDays(30)), bypassGovernance: false);

        var report = sweeper.Run(put.Value.At + TimeSpan.FromDays(10));

        Assert.Equal(0, report.Expired);
        Assert.Equal(VersionKind.Put, registry.GetCurrentKind(b, "k"));
    }

    [Fact]
    public void Skips_versions_with_legal_hold()
    {
        const string b = "lc-hold";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));
        registry.SetLifecycle(b, OneRule(prefix: "", days: 1));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        registry.PutLegalHold(b, "k", put.Value.VersionId, on: true);

        var report = sweeper.Run(put.Value.At + TimeSpan.FromDays(10));

        Assert.Equal(0, report.Expired);
        Assert.Equal(VersionKind.Put, registry.GetCurrentKind(b, "k"));
    }

    [Fact]
    public void Sweep_idempotent_does_not_stamp_marker_on_existing_marker()
    {
        const string b = "lc-idempotent";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetLifecycle(b, OneRule(prefix: "", days: 1));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        var now = put.Value.At + TimeSpan.FromDays(2);

        var first = sweeper.Run(now);
        Assert.Equal(1, first.Expired);
        var second = sweeper.Run(now);
        Assert.Equal(0, second.Expired);
    }

    [Fact]
    public void Expired_object_delete_marker_reaps_lone_marker()
    {
        const string b = "lc-marker-reap";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        registry.HardDeleteVersion(b, "k", put.Value.VersionId, bypassGovernance: false);
        registry.AppendDelete(b, "k", bypassGovernance: false);

        Assert.Equal(VersionKind.DeleteMarker, registry.GetCurrentKind(b, "k"));

        registry.SetLifecycle(b, new LifecycleConfig(new[]
        {
            new LifecycleRule("reap", true, "", null, ExpiredObjectDeleteMarker: true),
        }));

        var report = sweeper.Run(DateTimeOffset.UtcNow);
        Assert.Equal(1, report.MarkersReaped);
        Assert.Null(registry.GetCurrentKind(b, "k"));
    }

    [Fact]
    public void Expired_object_delete_marker_skips_when_other_versions_exist()
    {
        const string b = "lc-marker-skip";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);

        registry.AppendPut(b, "k", MakePut());
        registry.AppendDelete(b, "k", bypassGovernance: false);

        registry.SetLifecycle(b, new LifecycleConfig(new[]
        {
            new LifecycleRule("reap", true, "", null, ExpiredObjectDeleteMarker: true),
        }));

        var report = sweeper.Run(DateTimeOffset.UtcNow);
        Assert.Equal(0, report.MarkersReaped);
        Assert.Equal(VersionKind.DeleteMarker, registry.GetCurrentKind(b, "k"));
    }

    [Fact]
    public void Disabled_rule_is_ignored()
    {
        const string b = "lc-disabled";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetLifecycle(b, OneRule(prefix: "", days: 1, enabled: false));

        var put = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut()));
        var report = sweeper.Run(put.Value.At + TimeSpan.FromDays(10));

        Assert.Equal(0, report.Expired);
    }

    [Fact]
    public async System.Threading.Tasks.Task Lifecycle_xml_round_trips()
    {
        var input = """
            <?xml version="1.0"?>
            <LifecycleConfiguration>
              <Rule>
                <ID>r1</ID>
                <Filter><Prefix>logs/</Prefix></Filter>
                <Status>Enabled</Status>
                <Expiration><Days>30</Days></Expiration>
              </Rule>
              <Rule>
                <ID>r2</ID>
                <Filter><Prefix></Prefix></Filter>
                <Status>Enabled</Status>
                <Expiration><ExpiredObjectDeleteMarker>true</ExpiredObjectDeleteMarker></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var reader = new S3XmlReader();
        var parsed = await reader.ReadLifecycleConfiguration(new MemoryStream(Encoding.UTF8.GetBytes(input)), CancellationToken.None);
        var cfg = Assert.IsType<Result<LifecycleConfig>.Success>(parsed).Value;
        Assert.Equal(2, cfg.Rules.Count);
        Assert.Equal("logs/", cfg.Rules[0].Prefix);
        Assert.Equal(30, cfg.Rules[0].ExpirationDays);
        Assert.True(cfg.Rules[1].ExpiredObjectDeleteMarker);

        var writer = new S3XmlWriter();
        var sink = new MemoryStream();
        await writer.WriteLifecycleConfiguration(sink, cfg, CancellationToken.None);
        sink.Position = 0;
        var roundTripped = await reader.ReadLifecycleConfiguration(sink, CancellationToken.None);
        var cfg2 = Assert.IsType<Result<LifecycleConfig>.Success>(roundTripped).Value;
        Assert.Equal(cfg.Rules.Count, cfg2.Rules.Count);
        Assert.Equal(cfg.Rules[0].Prefix, cfg2.Rules[0].Prefix);
        Assert.Equal(cfg.Rules[0].ExpirationDays, cfg2.Rules[0].ExpirationDays);
        Assert.Equal(string.Empty, cfg2.Rules[1].Prefix);
        Assert.True(cfg2.Rules[1].ExpiredObjectDeleteMarker);
        Assert.Null(cfg2.Rules[1].ExpirationDays);
    }

    [Theory]
    [InlineData("<AbortIncompleteMultipartUpload><DaysAfterInitiation>7</DaysAfterInitiation></AbortIncompleteMultipartUpload>")]
    [InlineData("<Filter><Tag><Key>x</Key><Value>y</Value></Tag></Filter>")]
    [InlineData("<Filter><And><Prefix>p/</Prefix><Tag><Key>x</Key><Value>y</Value></Tag></And></Filter>")]
    public async System.Threading.Tasks.Task Unsupported_rule_elements_are_rejected(string extra)
    {
        var input = $"""
            <LifecycleConfiguration>
              <Rule>
                <ID>r</ID>
                <Status>Enabled</Status>
                {extra}
                <Expiration><Days>30</Days></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var reader = new S3XmlReader();
        var parsed = await reader.ReadLifecycleConfiguration(new MemoryStream(Encoding.UTF8.GetBytes(input)), CancellationToken.None);
        var err = Assert.IsType<Result<LifecycleConfig>.Failure>(parsed).Error;
        Assert.IsType<InvalidArgumentError>(err);
    }

    [Fact]
    public void Expires_noncurrent_version_on_versioned_bucket_after_noncurrent_days()
    {
        const string b = "lc-noncurrent";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetLifecycle(b, new LifecycleConfig([
            new LifecycleRule("prune-old", true, "data/", null, false, NoncurrentDays: 30),
        ]));

        var v1 = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "data/item.txt", MakePut("version1")));
        var v2 = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "data/item.txt", MakePut("version2")));

        var versionsBefore = Assert.IsType<Result<VersionsPage>.Success>(registry.ListAllVersions(b, "data/", null, 10)).Value;
        Assert.Equal(2, versionsBefore.Entries.Count);

        // 10 days after v2 was created: v1 is not yet 30 days noncurrent
        var t10 = v2.Value.At + TimeSpan.FromDays(10);
        var report1 = sweeper.Run(t10);
        Assert.Equal(0, report1.NoncurrentExpired);
        Assert.Equal(2, Assert.IsType<Result<VersionsPage>.Success>(registry.ListAllVersions(b, "data/", null, 10)).Value.Entries.Count);

        // 31 days after v2 was created: v1 has been noncurrent for 31 days
        var t31 = v2.Value.At + TimeSpan.FromDays(31);
        var report2 = sweeper.Run(t31);
        Assert.Equal(1, report2.NoncurrentExpired);

        var versionsAfter = Assert.IsType<Result<VersionsPage>.Success>(registry.ListAllVersions(b, "data/", null, 10)).Value.Entries;
        Assert.Single(versionsAfter);
        Assert.Equal(v2.Value.VersionId, versionsAfter[0].VersionId);
    }

    [Fact]
    public void Noncurrent_version_expiration_respects_legal_hold()
    {
        const string b = "lc-noncurrent-hold";
        registry.Create(b);
        registry.SetVersioning(b, VersioningStatus.Enabled);
        registry.SetObjectLock(b, new ObjectLockConfig(Enabled: true, Default: null));
        registry.SetLifecycle(b, new LifecycleConfig([
            new LifecycleRule("prune-old", true, "", null, false, NoncurrentDays: 30),
        ]));

        var v1 = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut("v1")));
        registry.PutLegalHold(b, "k", v1.Value.VersionId, on: true);
        var v2 = Assert.IsType<Result<PutEntry>.Success>(registry.AppendPut(b, "k", MakePut("v2")));

        var report = sweeper.Run(v2.Value.At + TimeSpan.FromDays(35));
        Assert.Equal(0, report.NoncurrentExpired);
        Assert.Equal(2, Assert.IsType<Result<VersionsPage>.Success>(registry.ListAllVersions(b, null, null, 10)).Value.Entries.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Noncurrent_version_xml_round_trips()
    {
        var input = """
            <LifecycleConfiguration>
              <Rule>
                <ID>nc-rule</ID>
                <Filter><Prefix>backups/</Prefix></Filter>
                <Status>Enabled</Status>
                <NoncurrentVersionExpiration>
                  <NoncurrentDays>30</NoncurrentDays>
                </NoncurrentVersionExpiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var reader = new S3XmlReader();
        var parsed = await reader.ReadLifecycleConfiguration(new MemoryStream(Encoding.UTF8.GetBytes(input)), CancellationToken.None);
        var cfg = Assert.IsType<Result<LifecycleConfig>.Success>(parsed).Value;
        Assert.Single(cfg.Rules);
        Assert.Equal("backups/", cfg.Rules[0].Prefix);
        Assert.Equal(30, cfg.Rules[0].NoncurrentDays);

        var writer = new S3XmlWriter();
        var sink = new MemoryStream();
        await writer.WriteLifecycleConfiguration(sink, cfg, CancellationToken.None);
        sink.Position = 0;

        var roundTripped = await reader.ReadLifecycleConfiguration(sink, CancellationToken.None);
        var cfg2 = Assert.IsType<Result<LifecycleConfig>.Success>(roundTripped).Value;
        Assert.Single(cfg2.Rules);
        Assert.Equal("backups/", cfg2.Rules[0].Prefix);
        Assert.Equal(30, cfg2.Rules[0].NoncurrentDays);
    }

    [Fact]
    public async System.Threading.Tasks.Task Transition_is_rejected()
    {
        var input = """
            <LifecycleConfiguration>
              <Rule>
                <ID>tier</ID>
                <Filter><Prefix></Prefix></Filter>
                <Status>Enabled</Status>
                <Transition><Days>30</Days><StorageClass>GLACIER</StorageClass></Transition>
              </Rule>
            </LifecycleConfiguration>
            """;

        var reader = new S3XmlReader();
        var parsed = await reader.ReadLifecycleConfiguration(new MemoryStream(Encoding.UTF8.GetBytes(input)), CancellationToken.None);
        var err = Assert.IsType<Result<LifecycleConfig>.Failure>(parsed).Error;
        Assert.IsType<InvalidArgumentError>(err);
    }
}
