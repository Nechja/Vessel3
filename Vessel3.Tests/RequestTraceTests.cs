using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public sealed class RequestTraceTests : IDisposable
{
    private readonly string root;
    private readonly BucketRegistry registry;
    private readonly BlobPool blobs;
    private readonly GcGate gate = new();

    public RequestTraceTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sync = new PortableFileSync();
        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, new DurableWrite(sync));
        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        Assert.IsType<Result<bool>.Success>(registry.Create("bucket"));
    }

    public void Dispose()
    {
        RequestTrace.Current = null;
        registry.Dispose();
        gate.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void Scopes_Accumulate_Into_The_Current_Trace()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;

        using (RequestTrace.Time(Stage.Body)) Thread.Sleep(2);
        using (RequestTrace.Time(Stage.Body)) Thread.Sleep(2);
        RequestTrace.Since(Stage.Query, Stopwatch.GetTimestamp() - Stopwatch.Frequency);

        Assert.True(trace.Ticks(Stage.Body) >= Stopwatch.Frequency / 1000);
        Assert.True(trace.Ticks(Stage.Query) >= Stopwatch.Frequency);
        Assert.Equal(0, trace.Ticks(Stage.LogSync));
    }

    [Fact]
    public void Without_A_Current_Trace_Scopes_Are_NoOps()
    {
        RequestTrace.Current = null;
        using (RequestTrace.Time(Stage.Body)) { }
        RequestTrace.Since(Stage.Query, Stopwatch.GetTimestamp());
    }

    [Fact]
    public async Task Trace_Flows_Across_Await_And_Into_Child_Tasks()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;

        await Task.Yield();
        await Task.Run(() => RequestTrace.Since(Stage.ReadLock, Stopwatch.GetTimestamp() - 100), TestContext.Current.CancellationToken);

        Assert.Same(trace, RequestTrace.Current);
        Assert.True(trace.Ticks(Stage.ReadLock) >= 100);
    }

    private sealed class FakeKeyAction : IS3KeyAction
    {
        public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.None);
        public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) => Task.FromResult(Results.Ok());
    }

    private sealed class FakeBucketAction : IS3BucketAction
    {
        public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.None);
        public Task<IResult> Invoke(string bucket, HttpContext ctx) => Task.FromResult(Results.Ok());
    }

    [Fact]
    public async Task Key_Dispatcher_Stamps_Action_Bucket_Key_And_Marks_Handled()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        var http = new HttpResultMapper(new S3XmlWriter());
        var dispatcher = new S3KeyActionDispatcher([new FakeKeyAction()], http);

        await dispatcher.Dispatch(HttpMethods.Put, "b1", "dir/k", new DefaultHttpContext());

        Assert.Equal("FakeKeyAction", trace.Action);
        Assert.Equal("b1", trace.Bucket);
        Assert.Equal("dir/k", trace.Key);
        Assert.True(trace.HandledTicks > 0);
    }

    [Fact]
    public async Task Bucket_Dispatcher_Stamps_Action_And_Bucket()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        var http = new HttpResultMapper(new S3XmlWriter());
        var dispatcher = new S3BucketActionDispatcher([new FakeBucketAction()], http);

        await dispatcher.Dispatch(HttpMethods.Get, "b2", new DefaultHttpContext());

        Assert.Equal("FakeBucketAction", trace.Action);
        Assert.Equal("b2", trace.Bucket);
        Assert.Null(trace.Key);
        Assert.True(trace.HandledTicks > 0);
    }

    [Fact]
    public async Task Unrouted_Request_Leaves_Action_Unset()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        var dispatcher = new S3KeyActionDispatcher([new FakeKeyAction()], new HttpResultMapper(new S3XmlWriter()));

        await dispatcher.Dispatch(HttpMethods.Delete, "b1", "k", new DefaultHttpContext());

        Assert.Equal("Other", trace.Action);
        Assert.Equal(0, trace.HandledTicks);
    }

    [Fact]
    public async Task Put_Records_Every_Write_Stage()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        var objects = new ObjectStore(registry, blobs, new PreconditionEvaluator(), gate);
        var payload = Encoding.UTF8.GetBytes("stage timing payload");

        Assert.IsType<Result<PutOutcome>.Success>(await objects.Put(
            "bucket", "k", new MemoryStream(payload), payload.Length, "text/plain",
            null, null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ChecksumSet.Empty, TestContext.Current.CancellationToken));

        Assert.True(trace.Ticks(Stage.Body) > 0);
        Assert.True(trace.Ticks(Stage.BlobSync) > 0);
        Assert.True(trace.Ticks(Stage.LogSync) > 0);
        Assert.True(trace.Ticks(Stage.IndexCommit) > 0);
        Assert.True(trace.Ticks(Stage.WriteLock) >= 0);
        Assert.True(trace.Ticks(Stage.ReadLock) >= 0);
        Assert.Equal(0, trace.Ticks(Stage.GateWait));
    }

    [Fact]
    public void List_And_Lookup_Record_Read_Stages()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;

        Assert.IsType<Result<CurrentPage>.Success>(registry.ListCurrent("bucket", "p/", null, 10));
        Assert.IsType<Result<PutEntry?>.Success>(registry.GetCurrentPut("bucket", "k"));

        Assert.True(trace.Ticks(Stage.Query) > 0);
        Assert.True(trace.Ticks(Stage.ReadLock) >= 0);
        Assert.Equal(0, trace.Ticks(Stage.Body));
    }

    [Fact]
    public async Task Queued_Writer_Records_Gate_Wait()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;

        var collecting = await gate.Collecting(TimeSpan.FromSeconds(5));
        Assert.NotNull(collecting);
        var writing = gate.Writing();
        Assert.False(writing.IsCompleted);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        collecting.Dispose();
        using var lease = await writing;

        Assert.True(trace.Ticks(Stage.GateWait) >= Stopwatch.Frequency / 100);
    }

    [Fact]
    public async Task Uncontended_Writer_Records_No_Gate_Wait()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        using var lease = await gate.Writing();
        Assert.Equal(0, trace.Ticks(Stage.GateWait));
    }

    [Fact]
    public async Task Contended_Write_Lock_Is_Measured()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = registry.OpenBuckets().Single();
        var writeGate = (Lock)typeof(Bucket).GetField("writeGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(bucket)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Task.Run(() =>
        {
            lock (writeGate)
            {
                entered.SetResult();
                release.Task.Wait(ct);
            }
        }, ct);
        await entered.Task;

        var trace = new RequestTrace();
        var putting = Task.Run(() =>
        {
            RequestTrace.Current = trace;
            return bucket.AppendPut("k2", new PutRequest("sha", "00", 1, "text/plain", new Dictionary<string, string>(), null, new Dictionary<string, string>()));
        }, ct);
        await Task.Delay(30, ct);
        release.SetResult();
        await holding;
        await putting;

        Assert.True(trace.Ticks(Stage.WriteLock) >= Stopwatch.Frequency / 100);
    }
}
