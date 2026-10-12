using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;
using Xunit;

namespace Vessel3.Tests;

public sealed class NativeMultipartUploadTests : IAsyncDisposable
{
    private readonly string testDir;
    private readonly List<WebApplication> runningApps = [];
    private readonly List<IDisposable> disposables = [];

    public NativeMultipartUploadTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-native-mp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in disposables)
        {
            client.Dispose();
        }

        foreach (var app in runningApps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<(WebApplication App, IVesselClient Client)> StartServer(string subDir)
    {
        var dataDir = Path.Combine(testDir, subDir);
        Directory.CreateDirectory(dataDir);

        var config = new VesselConfig(
            DataRoot: dataDir,
            AccessKey: null,
            SecretKey: null,
            Region: "us-east-1",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(120),
            LifecycleInterval: TimeSpan.FromHours(1),
            CompactInterval: TimeSpan.FromHours(1),
            CompactThresholdBytes: 64 * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(1),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();
        runningApps.Add(app);

        var client = new VesselClient(app.Urls.First());
        disposables.Add(client);
        return (app, client);
    }

    [Fact]
    public async Task MultipartUpload_CreateUploadPartsAndComplete_AssemblesObjectCorrectly()
    {
        var (_, client) = await StartServer("mp-lifecycle");
        var bucket = "mp-bucket";
        var key = "nested/test-file.bin";

        var createBucket = await client.CreateBucket(bucket);
        Assert.True(createBucket is Result.OkResult);

        var createRes = await client.CreateMultipartUpload(bucket, key, "application/octet-stream");
        Assert.True(createRes.TryGetValue(out var upload, out _));
        Assert.False(string.IsNullOrWhiteSpace(upload.UploadId));
        Assert.Equal(bucket, upload.Bucket);
        Assert.Equal(key, upload.Key);

        var part1Bytes = new byte[512 * 1024];
        var part2Bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(part1Bytes);
        Random.Shared.NextBytes(part2Bytes);

        using var part1Stream = new MemoryStream(part1Bytes);
        var part1Res = await client.UploadPart(bucket, key, upload.UploadId, 1, part1Stream, part1Bytes.Length);
        Assert.True(part1Res.TryGetValue(out var p1, out _));
        Assert.Equal(1, p1.PartNumber);
        Assert.False(string.IsNullOrWhiteSpace(p1.ETag));

        using var part2Stream = new MemoryStream(part2Bytes);
        var part2Res = await client.UploadPart(bucket, key, upload.UploadId, 2, part2Stream, part2Bytes.Length);
        Assert.True(part2Res.TryGetValue(out var p2, out _));
        Assert.Equal(2, p2.PartNumber);
        Assert.False(string.IsNullOrWhiteSpace(p2.ETag));

        var completeRes = await client.CompleteMultipartUpload(bucket, key, upload.UploadId, [p1, p2]);
        Assert.True(completeRes.TryGetValue(out var putResult, out _));
        Assert.Equal(part1Bytes.Length + part2Bytes.Length, putResult.Size);

        var statRes = await client.StatObject(bucket, key);
        Assert.True(statRes.TryGetValue(out var stat, out _));
        Assert.Equal(part1Bytes.Length + part2Bytes.Length, stat.Size);

        var getRes = await client.GetObject(bucket, key);
        Assert.True(getRes.TryGetValue(out var download, out _));
        Assert.Equal("application/octet-stream", download.ContentType);
        using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        var downloadedBytes = ms.ToArray();

        var expectedBytes = new byte[part1Bytes.Length + part2Bytes.Length];
        Buffer.BlockCopy(part1Bytes, 0, expectedBytes, 0, part1Bytes.Length);
        Buffer.BlockCopy(part2Bytes, 0, expectedBytes, part1Bytes.Length, part2Bytes.Length);
        Assert.Equal(expectedBytes, downloadedBytes);
    }

    [Fact]
    public async Task MultipartUpload_Abort_CancelsUploadAndPreventsCommit()
    {
        var (_, client) = await StartServer("mp-abort");
        var bucket = "mp-abort-bucket";
        var key = "cancelled.bin";

        var createBucket = await client.CreateBucket(bucket);
        Assert.True(createBucket is Result.OkResult);

        var createRes = await client.CreateMultipartUpload(bucket, key);
        Assert.True(createRes.TryGetValue(out var upload, out _));

        var partBytes = Encoding.UTF8.GetBytes("chunk to be aborted");
        using var partStream = new MemoryStream(partBytes);
        var partRes = await client.UploadPart(bucket, key, upload.UploadId, 1, partStream, partBytes.Length);
        Assert.True(partRes.TryGetValue(out var p1, out _));

        var abortRes = await client.AbortMultipartUpload(bucket, key, upload.UploadId);
        Assert.True(abortRes is Result.OkResult);

        var completeRes = await client.CompleteMultipartUpload(bucket, key, upload.UploadId, [p1]);
        Assert.False(completeRes.TryGetValue(out _, out var completeErr));
        Assert.Equal(404, completeErr.Status);

        var statRes = await client.StatObject(bucket, key);
        Assert.False(statRes.TryGetValue(out _, out var statErr));
        Assert.Equal(404, statErr.Status);
    }

    [Fact]
    public async Task UploadObject_SmallPayload_UsesDirectPut()
    {
        var (_, client) = await StartServer("upload-small");
        var bucket = "small-bucket";
        var key = "small.txt";

        var createBucket = await client.CreateBucket(bucket);
        Assert.True(createBucket is Result.OkResult);

        var content = Encoding.UTF8.GetBytes("small payload content");
        using var stream = new MemoryStream(content);
        var syncProgress = new SyncProgress();

        var res = await client.UploadObject(bucket, key, stream, content.Length, "text/plain", progress: syncProgress);
        Assert.True(res.TryGetValue(out var outcome, out _));
        Assert.Equal(content.Length, outcome.Size);

        var getRes = await client.GetObject(bucket, key);
        Assert.True(getRes.TryGetValue(out var download, out _));
        using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public async Task UploadObject_LargePayload_ChunksAndReportsProgress()
    {
        var (_, client) = await StartServer("upload-large");
        var bucket = "large-bucket";
        var key = "large.bin";

        var createBucket = await client.CreateBucket(bucket);
        Assert.True(createBucket is Result.OkResult);

        var totalSize = VesselClient.DefaultPartSize + (1024 * 1024);
        var data = new byte[totalSize];
        Random.Shared.NextBytes(data);

        using var stream = new MemoryStream(data);
        var syncProgress = new SyncProgress();

        var res = await client.UploadObject(bucket, key, stream, totalSize, "application/octet-stream", progress: syncProgress);
        Assert.True(res.TryGetValue(out var outcome, out _));
        Assert.Equal(totalSize, outcome.Size);

        Assert.Contains((long)VesselClient.DefaultPartSize, syncProgress.Reports);
        Assert.Equal((long)totalSize, syncProgress.Reports.Last());

        var getRes = await client.GetObject(bucket, key);
        Assert.True(getRes.TryGetValue(out var download, out _));
        using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        var downloaded = ms.ToArray();
        Assert.Equal(data.Length, downloaded.Length);
        Assert.Equal(data, downloaded);
    }

    private sealed class SyncProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];
        public void Report(long value) => Reports.Add(value);
    }
}
