using Vessel3.Server.Configuration;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class VolumeConfigurationTests
{
    private const string TestDataRoot = "/var/vessel3/data";

    [Fact]
    public void EmptyOrNull_ReturnsSingleDefaultIngestVolume()
    {
        Assert.True(VesselConfig.TryParseVolumes(null, TestDataRoot, out var vols1, out var err1));
        Assert.Null(err1);
        Assert.Single(vols1);
        Assert.Equal("default", vols1[0].Id);
        Assert.Equal(Path.Combine(TestDataRoot, "blobs"), vols1[0].Path);
        Assert.Equal(VolumeCapabilities.Ingest, vols1[0].Capabilities);
        Assert.True(vols1[0].IsWritable);

        Assert.True(VesselConfig.TryParseVolumes("   ", TestDataRoot, out var vols2, out var err2));
        Assert.Null(err2);
        Assert.Single(vols2);
        Assert.Equal("default", vols2[0].Id);
    }

    [Fact]
    public void FewerThanTwoTokens_FailsValidation()
    {
        Assert.False(VesselConfig.TryParseVolumes("single_token", TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("expected at least 'id:path'", err);
    }

    [Fact]
    public void EmptyVolumeId_FailsValidation()
    {
        Assert.False(VesselConfig.TryParseVolumes(":/data/blobs", TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("volume id cannot be empty", err);
    }

    [Fact]
    public void EmptyVolumePath_FailsValidation()
    {
        Assert.False(VesselConfig.TryParseVolumes("fast:", TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("volume path cannot be empty", err);
    }

    [Fact]
    public void DuplicateVolumeId_FailsValidation()
    {
        var raw = "fast:/mnt/nvme/fast:default:Ingest,fast:/mnt/nvme/fast2:default:Ingest";
        Assert.False(VesselConfig.TryParseVolumes(raw, TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("Duplicate volume id 'fast'", err);
    }

    [Fact]
    public void UnknownCapabilityFlag_FailsValidation()
    {
        var raw = "fast:/mnt/nvme/fast:default:OnDemnd";
        Assert.False(VesselConfig.TryParseVolumes(raw, TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("Invalid volume capability 'OnDemnd' for volume 'fast'", err);
    }

    [Fact]
    public void NoWritableVolumes_FailsValidation()
    {
        var raw = "archive:/mnt/archive:archive:ReadOnly,remote:/mnt/cloud:cloud:Remote";
        Assert.False(VesselConfig.TryParseVolumes(raw, TestDataRoot, out var vols, out var err));
        Assert.Null(vols);
        Assert.Contains("No writable volume configured", err);
    }

    [Fact]
    public void ValidMultiVolumeConfig_ParsesCorrectly()
    {
        var raw = "fast:/mnt/nvme:default:Ingest+Mirrored,bulk:/mnt/hdd:bulk:OnDemand,archive:/mnt/tape:archive:ReadOnly";
        Assert.True(VesselConfig.TryParseVolumes(raw, TestDataRoot, out var vols, out var err));
        Assert.Null(err);
        Assert.NotNull(vols);
        Assert.Equal(3, vols.Count);

        Assert.Equal("fast", vols[0].Id);
        Assert.Equal("/mnt/nvme", vols[0].Path);
        Assert.Equal("default", vols[0].Pool);
        Assert.True(vols[0].Capabilities.HasFlag(VolumeCapabilities.Ingest));
        Assert.True(vols[0].Capabilities.HasFlag(VolumeCapabilities.Mirrored));
        Assert.True(vols[0].IsWritable);

        Assert.Equal("bulk", vols[1].Id);
        Assert.Equal("/mnt/hdd", vols[1].Path);
        Assert.Equal("bulk", vols[1].Pool);
        Assert.True(vols[1].IsOnDemand);
        Assert.True(vols[1].IsWritable);

        Assert.Equal("archive", vols[2].Id);
        Assert.Equal("/mnt/tape", vols[2].Path);
        Assert.Equal("archive", vols[2].Pool);
        Assert.True(vols[2].IsReadOnly);
        Assert.False(vols[2].IsWritable);
    }

    [Fact]
    public void DefaultCapability_IsIngest_WhenOmitted()
    {
        var raw = "primary:/mnt/primary:default";
        Assert.True(VesselConfig.TryParseVolumes(raw, TestDataRoot, out var vols, out var err));
        Assert.Null(err);
        Assert.NotNull(vols);
        Assert.Single(vols);
        Assert.Equal("primary", vols[0].Id);
        Assert.Equal(VolumeCapabilities.Ingest, vols[0].Capabilities);
        Assert.True(vols[0].IsWritable);
    }
}
