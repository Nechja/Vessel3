using System.Text;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public class IncompleteBodyTests : IDisposable
{
    private readonly string root;
    private readonly BlobPool pool;

    public IncompleteBodyTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        pool = new BlobPool(new BlobPoolOptions(root), new PortableFileSync());
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private Task<Result<StoredBlob>> Write(string body, long? declaredSize) =>
        pool.Write(new MemoryStream(Encoding.UTF8.GetBytes(body)), declaredSize, ChecksumIntent.None,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Write_BodyShorterThanDeclared_IsRejected()
    {
        var written = await Write("hi", declaredSize: 5);
        Assert.IsType<Result<StoredBlob>.Failure>(written);
    }

    [Fact]
    public async Task Write_EmptyBodyWithDeclaredLength_IsRejected()
    {
        var written = await Write("", declaredSize: 5);
        Assert.IsType<Result<StoredBlob>.Failure>(written);
    }

    [Fact]
    public async Task Write_BodyLongerThanDeclared_IsRejected()
    {
        var written = await Write("far too much", declaredSize: 3);
        Assert.IsType<Result<StoredBlob>.Failure>(written);
    }

    [Fact]
    public async Task Write_RejectedBody_LeavesNoBlobBehind()
    {
        await Write("hi", declaredSize: 5);
        var strays = Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f).Length == 64).ToList()
            : [];
        Assert.Empty(strays);
    }

    [Fact]
    public async Task Write_BodyMatchingDeclaredLength_IsStored()
    {
        var written = await Write("hello", declaredSize: 5);
        Assert.IsType<Result<StoredBlob>.Success>(written);
    }

    [Fact]
    public async Task Write_BodyWithoutDeclaredLength_IsStored()
    {
        var written = await Write("no declared length", declaredSize: null);
        Assert.IsType<Result<StoredBlob>.Success>(written);
    }
}
