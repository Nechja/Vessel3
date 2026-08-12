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
}
