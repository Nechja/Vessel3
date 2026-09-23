using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class ReadGateTests : IDisposable
{
    private readonly string root;

    public ReadGateTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-readgate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Read_After_Dispose_Does_Not_Strand_The_Read_Gate()
    {
        var index = new BucketIndex(Path.Combine(root, "index.db"));
        index.Open();
        index.Dispose();

        Assert.ThrowsAny<Exception>(() => index.GetCurrentPut("k"));

        var secondReader = Task.Run(() => Assert.ThrowsAny<Exception>(() => index.GetCurrentPut("k")));
        var settled = await Task.WhenAny(secondReader, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(secondReader, settled);
    }

    [Fact]
    public async Task Multiple_Readers_Can_Execute_Concurrently()
    {
        var index = new BucketIndex(Path.Combine(root, "index.db"));
        index.Open();

        var reader1Holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(() =>
        {
            using var rh1 = index.ReadCmd();
            reader1Holding.SetResult();
            releaseReader1.Task.GetAwaiter().GetResult();
        }, TestContext.Current.CancellationToken);

        await reader1Holding.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Reader 2 runs while Reader 1 is still actively holding a ReadHandle
        var task2 = Task.Run(() => index.GetCurrentPut("k"), TestContext.Current.CancellationToken);
        var timeout = Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(task2, timeout);

        try
        {
            Assert.Same(task2, completed);
        }
        finally
        {
            releaseReader1.SetResult();
            await task1.WaitAsync(TestContext.Current.CancellationToken);
        }
    }
}
