using System.Text;
using Vessel3.Server;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class TagSetTests
{
    private static IReadOnlyDictionary<string, string> OkTags(Result<IReadOnlyDictionary<string, string>> r) =>
        ((Result<IReadOnlyDictionary<string, string>>.Success)r).Value;

    private static Error FailErr<T>(Result<T> r) =>
        ((Result<T>.Failure)r).Error;

    [Fact]
    public void Header_Empty_Returns_Empty()
    {
        var r = TagSet.ParseHeader(null);
        Assert.Empty(OkTags(r));
    }

    [Fact]
    public void Header_BasicPairs()
    {
        var r = TagSet.ParseHeader("a=1&b=2");
        var dict = OkTags(r);
        Assert.Equal("1", dict["a"]);
        Assert.Equal("2", dict["b"]);
    }

    [Fact]
    public void Header_UrlDecoded()
    {
        var r = TagSet.ParseHeader("city=New%20York&pct=50%25");
        var dict = OkTags(r);
        Assert.Equal("New York", dict["city"]);
        Assert.Equal("50%", dict["pct"]);
    }

    [Fact]
    public void Header_EmptyValue_Ok()
    {
        var r = TagSet.ParseHeader("k=");
        Assert.Equal("", OkTags(r)["k"]);
    }

    [Fact]
    public void Validate_TooMany_Fails()
    {
        var many = Enumerable.Range(0, 11)
            .Select(i => new KeyValuePair<string, string>("k" + i, "v"));
        Assert.IsType<InvalidTagError>(FailErr(TagSet.Validate(many)));
    }

    [Fact]
    public void Validate_EmptyKey_Fails()
    {
        Assert.IsType<InvalidTagError>(FailErr(TagSet.Validate(
            new[] { new KeyValuePair<string, string>("", "v") })));
    }

    [Fact]
    public void Validate_DuplicateKey_Fails()
    {
        Assert.IsType<InvalidTagError>(FailErr(TagSet.Validate(
            new[]
            {
                new KeyValuePair<string, string>("a", "1"),
                new KeyValuePair<string, string>("a", "2"),
            })));
    }

    [Fact]
    public void Validate_KeyTooLong_Fails()
    {
        var key = new string('x', 129);
        Assert.IsType<InvalidTagError>(FailErr(TagSet.Validate(
            new[] { new KeyValuePair<string, string>(key, "v") })));
    }

    [Fact]
    public void Validate_ValueTooLong_Fails()
    {
        var value = new string('x', 257);
        Assert.IsType<InvalidTagError>(FailErr(TagSet.Validate(
            new[] { new KeyValuePair<string, string>("k", value) })));
    }

    [Fact]
    public void Validate_AtBoundaries_Ok()
    {
        var pairs = new[]
        {
            new KeyValuePair<string, string>(new string('k', 128), new string('v', 256)),
        };
        Assert.Equal(128, OkTags(TagSet.Validate(pairs)).Keys.First().Length);
    }

    [Fact]
    public async Task Xml_RoundTrip()
    {
        var input = new Dictionary<string, string> { ["env"] = "prod", ["team"] = "platform" };
        var writer = new S3XmlWriter();
        using var ms = new MemoryStream();
        await writer.WriteTagging(ms, input, CancellationToken.None);
        ms.Position = 0;

        var reader = new S3XmlReader();
        var parsed = await reader.ReadTagging(ms, CancellationToken.None);
        var got = OkTags(parsed);
        Assert.Equal(input.Count, got.Count);
        foreach (var (k, v) in input) Assert.Equal(v, got[k]);
    }

    [Fact]
    public async Task Xml_Empty_TagSet()
    {
        const string xml = """<?xml version="1.0"?><Tagging><TagSet/></Tagging>""";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var reader = new S3XmlReader();
        var parsed = await reader.ReadTagging(ms, CancellationToken.None);
        Assert.Empty(OkTags(parsed));
    }

    [Fact]
    public async Task Xml_DuplicateKey_Fails()
    {
        const string xml = """
            <?xml version="1.0"?>
            <Tagging><TagSet>
              <Tag><Key>a</Key><Value>1</Value></Tag>
              <Tag><Key>a</Key><Value>2</Value></Tag>
            </TagSet></Tagging>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var reader = new S3XmlReader();
        var parsed = await reader.ReadTagging(ms, CancellationToken.None);
        Assert.IsType<InvalidTagError>(FailErr(parsed));
    }
}
