using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketNameTests
{
    private static readonly BucketRegistry Reg = new(new BucketRegistryOptions(
        Path.Combine(Path.GetTempPath(), "vessel3-name-tests-" + Guid.NewGuid().ToString("N"))), new PortableFileSync(), new DurableWrite(new PortableFileSync()));

    [Theory]
    [InlineData("vessel3-bench")]
    [InlineData("my.bucket")]
    [InlineData("abc")]
    [InlineData("a1-b2.c3")]
    public void Accepts_ValidNames(string name) =>
        Assert.True(Reg.IsValidName(name), $"expected '{name}' to be valid");

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("with/slash")]
    [InlineData("foo..bar")]
    public void Rejects_InvalidNames(string name) =>
        Assert.False(Reg.IsValidName(name), $"expected '{name}' to be invalid");
}
