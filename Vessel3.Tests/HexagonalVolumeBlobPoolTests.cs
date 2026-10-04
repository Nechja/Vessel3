using System.Text;
using Xunit;

namespace Vessel3.Tests;

public sealed class HexagonalVolumeBlobPoolTests : IDisposable
{
    private readonly string testRoot;
    private readonly string fastPath;
    private readonly string vaultPath;
    private readonly IFileSync sync = new PortableFileSync();

    public HexagonalVolumeBlobPoolTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"vessel3-hex-test-{Guid.NewGuid():N}");
        fastPath = Path.Combine(testRoot, "fast");
        vaultPath = Path.Combine(testRoot, "vault");
        Directory.CreateDirectory(fastPath);
        Directory.CreateDirectory(vaultPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(testRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task SingleVolume_BaselineBehavior_WriteAndReadSucceeds()
    {
        var pool = new BlobPool(new BlobPoolOptions(fastPath), sync);
        var data = "hello single volume world"u8.ToArray();

        var writeResult = await pool.Write(new MemoryStream(data), data.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeResult.TryGetValue(out var stored, out _));
        Assert.True(pool.Exists(stored.Sha));

        var openResult = pool.Open(stored.Sha);
        Assert.True(openResult.TryGetValue(out var stream, out _));
        using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(data, ms.ToArray());
        }

        var deleteResult = pool.Delete(stored.Sha);
        Assert.True(deleteResult.TryGetValue(out var deleted, out _));
        Assert.True(deleted);
        Assert.False(pool.Exists(stored.Sha));
    }

    [Fact]
    public async Task MultiVolume_IngestTargeting_WritesToIngestVolume()
    {
        var fastVol = new StorageVolume("fast", fastPath, "default", VolumeCapabilities.Ingest);
        var vaultVol = new StorageVolume("vault", vaultPath, "vault", VolumeCapabilities.OnDemand);
        var catalog = new MemoryBlobLocationCatalog();
        var registry = new VolumeRegistry([fastVol, vaultVol], sync);
        var pool = new BlobPool(registry, catalog);

        var data = "ingest targeting test payload"u8.ToArray();
        var writeResult = await pool.Write(new MemoryStream(data), data.Length, ChecksumIntent.All, CancellationToken.None);

        Assert.True(writeResult.TryGetValue(out var stored, out _));
        Assert.Equal("fast", catalog.LocateBlob(stored.Sha));

        var fastBlobFile = Path.Combine(fastPath, stored.Sha[..2], stored.Sha[2..4], stored.Sha);
        var vaultBlobFile = Path.Combine(vaultPath, stored.Sha[..2], stored.Sha[2..4], stored.Sha);

        Assert.True(File.Exists(fastBlobFile));
        Assert.False(File.Exists(vaultBlobFile));
    }

    [Fact]
    public async Task MultiVolume_ReadPriority_ProbesIngestBeforeOnDemand()
    {
        var fastVol = new StorageVolume("fast", fastPath, "default", VolumeCapabilities.Ingest);
        var vaultVol = new StorageVolume("vault", vaultPath, "vault", VolumeCapabilities.OnDemand);
        var catalog = new MemoryBlobLocationCatalog();
        var registry = new VolumeRegistry([fastVol, vaultVol], sync);
        var pool = new BlobPool(registry, catalog);

        var data = "vault historical data payload"u8.ToArray();
        var vaultStorage = registry.GetStorage("vault");
        var directWrite = await vaultStorage.WriteStagedBlobAsync(new MemoryStream(data), data.Length, ChecksumIntent.All, CancellationToken.None);

        Assert.True(directWrite.TryGetValue(out var stored, out _));
        Assert.Null(catalog.LocateBlob(stored.Sha));

        var openResult = pool.Open(stored.Sha);
        Assert.True(openResult.TryGetValue(out var stream, out _));
        using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(data, ms.ToArray());
        }

        Assert.Equal("vault", catalog.LocateBlob(stored.Sha));
    }

    [Fact]
    public async Task MultiVolume_EnumerateShardsAndBlobs_MergesAcrossAllVolumes()
    {
        var fastVol = new StorageVolume("fast", fastPath, "default", VolumeCapabilities.Ingest);
        var vaultVol = new StorageVolume("vault", vaultPath, "vault", VolumeCapabilities.OnDemand);
        var catalog = new MemoryBlobLocationCatalog();
        var registry = new VolumeRegistry([fastVol, vaultVol], sync);
        var pool = new BlobPool(registry, catalog);

        var data1 = "first blob in fast"u8.ToArray();
        var data2 = "second blob in vault"u8.ToArray();

        var w1 = await registry.GetStorage("fast").WriteStagedBlobAsync(new MemoryStream(data1), data1.Length, ChecksumIntent.All, CancellationToken.None);
        var w2 = await registry.GetStorage("vault").WriteStagedBlobAsync(new MemoryStream(data2), data2.Length, ChecksumIntent.All, CancellationToken.None);

        Assert.True(w1.TryGetValue(out var s1, out _));
        Assert.True(w2.TryGetValue(out var s2, out _));

        var shards = pool.EnumerateShards().ToList();
        Assert.Contains(s1.Sha[..2], shards);
        Assert.Contains(s2.Sha[..2], shards);

        var shas1 = pool.Enumerate(s1.Sha[..2]).ToList();
        Assert.Contains(s1.Sha, shas1);

        var shas2 = pool.Enumerate(s2.Sha[..2]).ToList();
        Assert.Contains(s2.Sha, shas2);
    }

    [Fact]
    public async Task MultiVolume_Delete_RemovesBlobFromWhicheverVolumeHoldsIt()
    {
        var fastVol = new StorageVolume("fast", fastPath, "default", VolumeCapabilities.Ingest);
        var vaultVol = new StorageVolume("vault", vaultPath, "vault", VolumeCapabilities.OnDemand);
        var catalog = new MemoryBlobLocationCatalog();
        var registry = new VolumeRegistry([fastVol, vaultVol], sync);
        var pool = new BlobPool(registry, catalog);

        var data = "blob to be deleted"u8.ToArray();
        var w = await registry.GetStorage("vault").WriteStagedBlobAsync(new MemoryStream(data), data.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(w.TryGetValue(out var s, out _));

        Assert.True(pool.Exists(s.Sha));
        var del = pool.Delete(s.Sha);
        Assert.True(del.TryGetValue(out var deleted, out _));
        Assert.True(deleted);
        Assert.False(pool.Exists(s.Sha));
        Assert.Null(catalog.LocateBlob(s.Sha));
    }

    [Fact]
    public void MultiVolume_ReapAbandonedTempFiles_CleansAcrossAllVolumes()
    {
        var fastVol = new StorageVolume("fast", fastPath, "default", VolumeCapabilities.Ingest);
        var vaultVol = new StorageVolume("vault", vaultPath, "vault", VolumeCapabilities.OnDemand);
        var registry = new VolumeRegistry([fastVol, vaultVol], sync);
        var pool = new BlobPool(registry, new MemoryBlobLocationCatalog());

        Directory.CreateDirectory(fastVol.TmpDir);
        Directory.CreateDirectory(vaultVol.TmpDir);

        var temp1 = Path.Combine(fastVol.TmpDir, "abandoned1");
        var temp2 = Path.Combine(vaultVol.TmpDir, "abandoned2");

        File.WriteAllText(temp1, "stale temp data");
        File.WriteAllText(temp2, "stale temp data");

        File.SetLastWriteTimeUtc(temp1, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(temp2, DateTime.UtcNow.AddHours(-2));

        var reaped = pool.ReapAbandonedTempFiles(DateTime.UtcNow.AddHours(-1));
        Assert.Equal(2, reaped);
        Assert.False(File.Exists(temp1));
        Assert.False(File.Exists(temp2));
    }
}
