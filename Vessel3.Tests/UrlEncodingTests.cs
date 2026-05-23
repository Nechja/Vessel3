using System.Xml.Linq;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class UrlEncodingTests
{
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static async Task<XDocument> WriteList(ListRequest req, ListPage page)
    {
        var writer = new S3XmlWriter();
        using var ms = new MemoryStream();
        await writer.WriteListObjects(ms, req, page, CancellationToken.None);
        ms.Position = 0;
        return XDocument.Load(ms);
    }

    private static ListRequest Req(string? prefix = null, string? delimiter = null,
        string? startAfter = null, string? encodingType = "url") =>
        new("bucket", prefix, delimiter, startAfter, 1000, IsV1: false, Marker: null, EncodingType: encodingType);

    private static ListPage Page(params ListEntry[] entries) =>
        new(entries, IsTruncated: false, NextContinuationToken: null, LastKey: null, KeyCount: entries.Length);

    private static string? ElementText(XDocument doc, string name) =>
        doc.Descendants(Ns + name).FirstOrDefault()?.Value;

    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("plus+sign", "plus%2Bsign")]
    [InlineData("a/b/c", "a%2Fb%2Fc")]
    [InlineData("x&y", "x%26y")]
    [InlineData("café", "caf%C3%A9")]
    [InlineData("ctlend", "ctl%01end")]
    [InlineData("plain-key_1.txt", "plain-key_1.txt")]
    public async Task ListObjects_Key_IsPercentEncoded(string rawKey, string expectedEncoded)
    {
        var page = Page(new ListEntry.Contents(rawKey, 3, DateTimeOffset.UnixEpoch, "etag"));
        var doc = await WriteList(Req(), page);

        Assert.Equal(expectedEncoded, ElementText(doc, "Key"));
    }

    [Fact]
    public async Task ListObjects_Key_NotEncodedWhenEncodingTypeAbsent()
    {
        var page = Page(new ListEntry.Contents("a b/c", 3, DateTimeOffset.UnixEpoch, "etag"));
        var doc = await WriteList(Req(encodingType: null), page);

        Assert.Equal("a b/c", ElementText(doc, "Key"));
        Assert.Null(ElementText(doc, "EncodingType"));
    }

    [Fact]
    public async Task ListObjects_EncodingTypeElement_EmittedWhenUrl()
    {
        var doc = await WriteList(Req(), Page());
        Assert.Equal("url", ElementText(doc, "EncodingType"));
    }

    [Fact]
    public async Task ListObjects_Prefix_IsPercentEncoded()
    {
        var doc = await WriteList(Req(prefix: "my dir/"), Page());
        Assert.Equal("my%20dir%2F", ElementText(doc, "Prefix"));
    }

    [Fact]
    public async Task ListObjects_Delimiter_IsPercentEncoded()
    {
        var doc = await WriteList(Req(delimiter: " "), Page());
        Assert.Equal("%20", ElementText(doc, "Delimiter"));
    }

    [Fact]
    public async Task ListObjects_StartAfter_IsPercentEncoded()
    {
        var doc = await WriteList(Req(startAfter: "a b"), Page());
        Assert.Equal("a%20b", ElementText(doc, "StartAfter"));
    }

    [Fact]
    public async Task ListObjects_CommonPrefix_IsPercentEncoded()
    {
        var page = Page(new ListEntry.CommonPrefix("photos 2024/"));
        var doc = await WriteList(Req(delimiter: "/"), page);

        var commonPrefix = doc.Descendants(Ns + "CommonPrefixes").Single();
        Assert.Equal("photos%202024%2F", commonPrefix.Element(Ns + "Prefix")!.Value);
    }

    [Fact]
    public async Task ListObjects_NextContinuationToken_NotEncoded()
    {
        var req = Req();
        var page = new ListPage(
            Array.Empty<ListEntry>(), IsTruncated: true,
            NextContinuationToken: "tok en", LastKey: null, KeyCount: 0);
        var doc = await WriteList(req, page);

        Assert.Equal("tok en", ElementText(doc, "NextContinuationToken"));
    }

    [Fact]
    public async Task ListObjectsV1_NextMarker_IsPercentEncoded()
    {
        var req = new ListRequest("bucket", null, null, null, 1000,
            IsV1: true, Marker: "m k", EncodingType: "url");
        var page = new ListPage(
            Array.Empty<ListEntry>(), IsTruncated: true,
            NextContinuationToken: null, LastKey: "last key", KeyCount: 0);

        var writer = new S3XmlWriter();
        using var ms = new MemoryStream();
        await writer.WriteListObjects(ms, req, page, CancellationToken.None);
        ms.Position = 0;
        var doc = XDocument.Load(ms);

        Assert.Equal("m%20k", ElementText(doc, "Marker"));
        Assert.Equal("last%20key", ElementText(doc, "NextMarker"));
    }

    [Fact]
    public async Task ListVersions_Key_And_NextKeyMarker_IsPercentEncoded()
    {
        var entries = new List<AllVersionsEntry>
        {
            new AllVersionsEntry.Put(
                Key: "ver key", VersionId: "v1", At: DateTimeOffset.UnixEpoch, IsLatest: true,
                Md5: "0123456789abcdef0123456789abcdef", Size: 1, Parts: null),
        };

        var writer = new S3XmlWriter();
        using var ms = new MemoryStream();
        await writer.WriteListVersions(ms, "bucket", "pre fix", entries,
            isTruncated: true, maxKeys: 1, encodingType: "url", CancellationToken.None);
        ms.Position = 0;
        var doc = XDocument.Load(ms);

        Assert.Equal("pre%20fix", ElementText(doc, "Prefix"));
        Assert.Equal("url", ElementText(doc, "EncodingType"));
        Assert.Equal("ver%20key", doc.Descendants(Ns + "Version").Single().Element(Ns + "Key")!.Value);
        Assert.Equal("ver%20key", ElementText(doc, "NextKeyMarker"));
    }

    [Fact]
    public async Task ListVersions_NotEncodedWhenEncodingTypeAbsent()
    {
        var entries = new List<AllVersionsEntry>
        {
            new AllVersionsEntry.Marker(
                Key: "mark key", VersionId: "v1", At: DateTimeOffset.UnixEpoch, IsLatest: true),
        };

        var writer = new S3XmlWriter();
        using var ms = new MemoryStream();
        await writer.WriteListVersions(ms, "bucket", null, entries,
            isTruncated: false, maxKeys: 1, encodingType: null, CancellationToken.None);
        ms.Position = 0;
        var doc = XDocument.Load(ms);

        Assert.Equal("mark key", doc.Descendants(Ns + "DeleteMarker").Single().Element(Ns + "Key")!.Value);
        Assert.Null(ElementText(doc, "EncodingType"));
    }
}
