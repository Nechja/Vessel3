using System.Text;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class S3XmlReaderTests
{
    private static Stream S(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task BatchDelete_ParsesObjects()
    {
        const string xml = "<Delete><Object><Key>a.txt</Key></Object><Object><Key>b.txt</Key></Object><Quiet>true</Quiet></Delete>";
        var r = await new S3XmlReader().ReadBatchDeleteRequest(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<BatchDeleteRequest>.Success>(r);
        Assert.True(ok.Value.Quiet);
        Assert.Equal(2, ok.Value.Keys.Count);
        Assert.Equal("a.txt", ok.Value.Keys[0].Key);
        Assert.Equal("b.txt", ok.Value.Keys[1].Key);
    }

    [Fact]
    public async Task BatchDelete_ParsesVersionId()
    {
        const string xml = "<Delete><Object><Key>b.txt</Key><VersionId>v1</VersionId></Object></Delete>";
        var r = await new S3XmlReader().ReadBatchDeleteRequest(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<BatchDeleteRequest>.Success>(r);
        Assert.Equal("v1", ok.Value.Keys[0].VersionId);
    }

    [Fact]
    public async Task BatchDelete_QuietBeforeObject_ParsesKeys()
    {
        const string xml = "<Delete><Quiet>false</Quiet><Object><Key>a.txt</Key></Object><Object><Key>b.txt</Key></Object></Delete>";
        var r = await new S3XmlReader().ReadBatchDeleteRequest(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<BatchDeleteRequest>.Success>(r);
        Assert.False(ok.Value.Quiet);
        Assert.Equal(2, ok.Value.Keys.Count);
        Assert.Equal("a.txt", ok.Value.Keys[0].Key);
        Assert.Equal("b.txt", ok.Value.Keys[1].Key);
    }

    [Fact]
    public async Task BatchDelete_MalformedXml_Fails()
    {
        var r = await new S3XmlReader().ReadBatchDeleteRequest(S("<Delete><Object"), CancellationToken.None);
        Assert.IsType<Result<BatchDeleteRequest>.Failure>(r);
    }

    [Fact]
    public async Task CompleteMultipart_ParsesParts()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>"etag1"</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>"etag2"</ETag></Part>
            </CompleteMultipartUpload>
            """;
        var r = await new S3XmlReader().ReadCompleteMultipartUploadRequest(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<IReadOnlyList<CompletedPart>>.Success>(r);
        Assert.Equal(2, ok.Value.Count);
        Assert.Equal(1, ok.Value[0].Number);
        Assert.Equal("etag1", ok.Value[0].Etag);
        Assert.Equal("etag2", ok.Value[1].Etag);
    }

    [Fact]
    public async Task VersioningConfig_Enabled()
    {
        var xml = """
            <VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Status>Enabled</Status>
            </VersioningConfiguration>
            """;
        var r = await new S3XmlReader().ReadVersioningConfiguration(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<VersioningStatus>.Success>(r);
        Assert.Equal(VersioningStatus.Enabled, ok.Value);
    }

    [Fact]
    public async Task VersioningConfig_Suspended()
    {
        var xml = """
            <VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Status>Suspended</Status>
            </VersioningConfiguration>
            """;
        var r = await new S3XmlReader().ReadVersioningConfiguration(S(xml), CancellationToken.None);
        var ok = Assert.IsType<Result<VersioningStatus>.Success>(r);
        Assert.Equal(VersioningStatus.Suspended, ok.Value);
    }

    [Fact]
    public async Task VersioningConfig_UnknownStatus_Fails()
    {
        var xml = """
            <VersioningConfiguration><Status>Banana</Status></VersioningConfiguration>
            """;
        var r = await new S3XmlReader().ReadVersioningConfiguration(S(xml), CancellationToken.None);
        Assert.IsType<Result<VersioningStatus>.Failure>(r);
    }

    private sealed class AsyncOnlyDripStream(byte[] data, int chunk) : Stream
    {
        private int pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override int ReadByte() => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = Math.Min(chunk, Math.Min(buffer.Length, data.Length - pos));
            data.AsMemory(pos, n).CopyTo(buffer);
            pos += n;
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    private static Stream Drip(string xml, int chunk) => new AsyncOnlyDripStream(Encoding.UTF8.GetBytes(xml), chunk);

    public static TheoryData<int> Chunks => new() { 1, 7, 4096, 1 << 20 };

    [Theory, MemberData(nameof(Chunks))]
    public async Task CompleteMultipart_AsyncOnlyBody(int chunk)
    {
        var sb = new StringBuilder("<CompleteMultipartUpload>");
        for (var i = 1; i <= 500; i++)
            sb.Append("<Part><PartNumber>").Append(i).Append("</PartNumber><ETag>\"").Append('e', 3000).Append(i).Append("\"</ETag><ChecksumSHA256>").Append(Convert.ToBase64String(new byte[32])).Append("</ChecksumSHA256></Part>");
        sb.Append("</CompleteMultipartUpload>");

        var r = await new S3XmlReader().ReadCompleteMultipartUploadRequest(Drip(sb.ToString(), chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<IReadOnlyList<CompletedPart>>.Success>(r);
        Assert.Equal(500, ok.Value.Count);
        Assert.Equal(1, ok.Value[0].Number);
        Assert.Equal(new string('e', 3000) + "1", ok.Value[0].Etag);
        Assert.Equal(500, ok.Value[499].Number);
        Assert.Equal(new string('e', 3000) + "500", ok.Value[499].Etag);
        Assert.Equal(new string('0', 64), ok.Value[499].Sums!.Sha256);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task BatchDelete_AsyncOnlyBody(int chunk)
    {
        var sb = new StringBuilder("<Delete><Quiet>true</Quiet>");
        for (var i = 0; i < 1000; i++)
            sb.Append("<Object><Key>").Append('k', 900).Append(i).Append("</Key><VersionId>v").Append(i).Append("</VersionId></Object>");
        sb.Append("</Delete>");

        var r = await new S3XmlReader().ReadBatchDeleteRequest(Drip(sb.ToString(), chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<BatchDeleteRequest>.Success>(r);
        Assert.True(ok.Value.Quiet);
        Assert.Equal(1000, ok.Value.Keys.Count);
        Assert.Equal(new string('k', 900) + "999", ok.Value.Keys[999].Key);
        Assert.Equal("v999", ok.Value.Keys[999].VersionId);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task VersioningConfig_AsyncOnlyBody(int chunk)
    {
        const string xml = "<VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Status>Enabled</Status></VersioningConfiguration>";
        var r = await new S3XmlReader().ReadVersioningConfiguration(Drip(xml, chunk), CancellationToken.None);
        Assert.Equal(VersioningStatus.Enabled, Assert.IsType<Result<VersioningStatus>.Success>(r).Value);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task Tagging_AsyncOnlyBody(int chunk)
    {
        var sb = new StringBuilder("<Tagging><TagSet>");
        for (var i = 0; i < 10; i++)
            sb.Append("<Tag><Key>").Append('k', 120).Append(i).Append("</Key><Value>").Append('v', 250).Append(i).Append("</Value></Tag>");
        sb.Append("</TagSet></Tagging>");

        var r = await new S3XmlReader().ReadTagging(Drip(sb.ToString(), chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<IReadOnlyDictionary<string, string>>.Success>(r);
        Assert.Equal(10, ok.Value.Count);
        Assert.Equal(new string('v', 250) + "9", ok.Value[new string('k', 120) + "9"]);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task ObjectLockConfig_AsyncOnlyBody(int chunk)
    {
        const string xml = "<ObjectLockConfiguration><ObjectLockEnabled>Enabled</ObjectLockEnabled><Rule><DefaultRetention><Mode>GOVERNANCE</Mode><Days>30</Days></DefaultRetention></Rule></ObjectLockConfiguration>";
        var r = await new S3XmlReader().ReadObjectLockConfiguration(Drip(xml, chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<ObjectLockConfig>.Success>(r);
        Assert.True(ok.Value.Enabled);
        Assert.Equal(RetentionMode.Governance, ok.Value.Default!.Mode);
        Assert.Equal(30, ok.Value.Default.Days);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task LifecycleConfig_AsyncOnlyBody(int chunk)
    {
        var sb = new StringBuilder("<LifecycleConfiguration>");
        for (var i = 0; i < 50; i++)
            sb.Append("<Rule><ID>").Append('r', 200).Append(i).Append("</ID><Filter><Prefix>").Append('p', 500).Append(i).Append("/</Prefix></Filter><Status>Enabled</Status><Expiration><Days>").Append(i + 1).Append("</Days></Expiration></Rule>");
        sb.Append("</LifecycleConfiguration>");

        var r = await new S3XmlReader().ReadLifecycleConfiguration(Drip(sb.ToString(), chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<Vessel3.Server.Lifecycle.LifecycleConfig>.Success>(r);
        Assert.Equal(50, ok.Value.Rules.Count);
        Assert.Equal(new string('r', 200) + "49", ok.Value.Rules[49].Id);
        Assert.Equal(new string('p', 500) + "49/", ok.Value.Rules[49].Prefix);
        Assert.Equal(50, ok.Value.Rules[49].ExpirationDays);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task Retention_AsyncOnlyBody(int chunk)
    {
        const string xml = "<Retention><Mode>COMPLIANCE</Mode><RetainUntilDate>2030-01-02T03:04:05Z</RetainUntilDate></Retention>";
        var r = await new S3XmlReader().ReadRetention(Drip(xml, chunk), CancellationToken.None);
        var ok = Assert.IsType<Result<Retention>.Success>(r);
        Assert.Equal(RetentionMode.Compliance, ok.Value.Mode);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), ok.Value.RetainUntilDate);
    }

    [Theory, MemberData(nameof(Chunks))]
    public async Task LegalHold_AsyncOnlyBody(int chunk)
    {
        var r = await new S3XmlReader().ReadLegalHold(Drip("<LegalHold><Status>ON</Status></LegalHold>", chunk), CancellationToken.None);
        Assert.True(Assert.IsType<Result<bool>.Success>(r).Value);
    }
}
