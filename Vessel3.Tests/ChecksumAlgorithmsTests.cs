using System.Text;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public sealed class ChecksumAlgorithmsTests
{
    // Textbook empty-input vectors — stable anchors that need no external compute.
    [Fact]
    public void ComputeAll_EmptyInput_MatchesKnownVectors()
    {
        var (crc32, crc32c, sha1, sha256) = ChecksumAlgorithms.ComputeAll(ReadOnlySpan<byte>.Empty);

        Assert.Equal("00000000", crc32);
        Assert.Equal("00000000", crc32c);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", sha1);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha256);
    }

    // "123456789" is the canonical CRC test string; CRC32 (zlib) and CRC32C (Castagnoli)
    // have well-known check values: 0xCBF43926 and 0xE3069283 respectively.
    [Fact]
    public void ComputeAll_Check123456789_MatchesKnownCrcVectors()
    {
        var data = Encoding.ASCII.GetBytes("123456789");
        var (crc32, crc32c, sha1, sha256) = ChecksumAlgorithms.ComputeAll(data);

        Assert.Equal("cbf43926", crc32);
        Assert.Equal("e3069283", crc32c);
        // SHA1/SHA256 of "123456789" — standard reference digests.
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", sha1);
        Assert.Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", sha256);
    }

    [Fact]
    public void CrcUInt32ToHex_IsBigEndian()
    {
        Assert.Equal("cbf43926", ChecksumAlgorithms.CrcUInt32ToHex(0xCBF43926u));
        Assert.Equal("00000001", ChecksumAlgorithms.CrcUInt32ToHex(1u));
        Assert.Equal("ffffffff", ChecksumAlgorithms.CrcUInt32ToHex(0xFFFFFFFFu));
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("cbf43926")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    public void HexToBase64_RoundTripsThroughBase64ToHex(string hex)
    {
        var b64 = ChecksumAlgorithms.HexToBase64(hex);
        var back = ChecksumAlgorithms.Base64ToHex(b64);
        Assert.Equal(hex, back);
    }

    [Fact]
    public void HexToBase64_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", ChecksumAlgorithms.HexToBase64(""));
    }

    [Fact]
    public void HexToBase64_KnownValue()
    {
        // SHA256("") raw bytes base64-encoded — the S3 wire form.
        var b64 = ChecksumAlgorithms.HexToBase64("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        Assert.Equal("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", b64);
    }

    [Fact]
    public void Base64ToHex_MalformedInput_ReturnsNull()
    {
        Assert.Null(ChecksumAlgorithms.Base64ToHex("not valid base64!!!"));
    }

    [Fact]
    public void Base64ToHex_EmptyInput_ReturnsNull()
    {
        Assert.Null(ChecksumAlgorithms.Base64ToHex(""));
    }

    [Theory]
    [InlineData("CRC32", "Crc32")]
    [InlineData("crc32c", "Crc32C")]
    [InlineData("  SHA1 ", "Sha1")]
    [InlineData("sha256", "Sha256")]
    public void TryParseName_ValidNames_Parse(string name, string expected)
    {
        Assert.True(ChecksumAlgorithms.TryParseName(name, out var algo));
        Assert.Equal(expected, algo.ToString());
    }

    [Theory]
    [InlineData("md5")]
    [InlineData("")]
    [InlineData("crc64")]
    public void TryParseName_InvalidNames_Fail(string name)
    {
        Assert.False(ChecksumAlgorithms.TryParseName(name, out _));
    }

    [Fact]
    public void HeaderFor_MapsToWireHeaderName()
    {
        Assert.Equal(ChecksumAlgorithms.HeaderCrc32, ChecksumAlgorithms.HeaderFor(ChecksumAlgorithm.Crc32));
        Assert.Equal(ChecksumAlgorithms.HeaderCrc32C, ChecksumAlgorithms.HeaderFor(ChecksumAlgorithm.Crc32C));
        Assert.Equal(ChecksumAlgorithms.HeaderSha1, ChecksumAlgorithms.HeaderFor(ChecksumAlgorithm.Sha1));
        Assert.Equal(ChecksumAlgorithms.HeaderSha256, ChecksumAlgorithms.HeaderFor(ChecksumAlgorithm.Sha256));
    }

    // A single-part composite must equal the hash of that one part's raw bytes.
    [Theory]
    [InlineData("Sha1")]
    [InlineData("Sha256")]
    [InlineData("Crc32")]
    [InlineData("Crc32C")]
    public void Composite_SinglePart_EqualsHashOfThatPartsBytes(string algoName)
    {
        var algo = Enum.Parse<ChecksumAlgorithm>(algoName);
        var data = Encoding.ASCII.GetBytes("123456789");
        var (crc32, crc32c, sha1, sha256) = ChecksumAlgorithms.ComputeAll(data);
        // The hex of the part, fed as the lone part to Composite.
        var partHex = algo switch
        {
            ChecksumAlgorithm.Crc32 => crc32,
            ChecksumAlgorithm.Crc32C => crc32c,
            ChecksumAlgorithm.Sha1 => sha1,
            ChecksumAlgorithm.Sha256 => sha256,
            _ => throw new InvalidOperationException(),
        };

        var composite = ChecksumAlgorithms.Composite(algo, new[] { partHex });

        // Composite hashes the decoded part-hex bytes; verify it equals hashing
        // the raw decoded bytes directly for that algorithm.
        var expected = algo switch
        {
            ChecksumAlgorithm.Crc32 => ChecksumAlgorithms.CrcUInt32ToHex(
                System.IO.Hashing.Crc32.HashToUInt32(Convert.FromHexString(partHex))),
            ChecksumAlgorithm.Crc32C => ChecksumAlgorithms.CrcUInt32ToHex(
                Crc32C.HashToUInt32(Convert.FromHexString(partHex))),
            ChecksumAlgorithm.Sha1 => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA1.HashData(Convert.FromHexString(partHex))),
            ChecksumAlgorithm.Sha256 => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(partHex))),
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(expected, composite);
    }

    [Fact]
    public void Composite_TwoParts_DiffersFromSinglePart()
    {
        var (_, _, oneHex, _) = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("part-one"));
        var (_, _, twoHex, _) = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("part-two"));

        var single = ChecksumAlgorithms.Composite(ChecksumAlgorithm.Sha1, new[] { oneHex });
        var pair = ChecksumAlgorithms.Composite(ChecksumAlgorithm.Sha1, new[] { oneHex, twoHex });

        Assert.NotEqual(single, pair);
    }

    // Verify-mismatch: a declared checksum that disagrees with the computed one.
    [Fact]
    public void ComputeAll_MismatchDetected()
    {
        var (_, _, _, sha256) = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("the real payload"));
        var declaredWrong = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("a different payload")).Sha256;

        Assert.NotEqual(declaredWrong, sha256);

        // Same payload always agrees with itself — the verify-match case.
        var (_, _, _, sha256Again) = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("the real payload"));
        Assert.Equal(sha256, sha256Again);
    }

    [Fact]
    public void ChecksumSet_Get_ReturnsPerAlgorithmValue()
    {
        var set = new ChecksumSet(Crc32: "aa", Crc32C: "bb", Sha1: "cc", Sha256: "dd");
        Assert.Equal("aa", set.Get(ChecksumAlgorithm.Crc32));
        Assert.Equal("bb", set.Get(ChecksumAlgorithm.Crc32C));
        Assert.Equal("cc", set.Get(ChecksumAlgorithm.Sha1));
        Assert.Equal("dd", set.Get(ChecksumAlgorithm.Sha256));
    }

    [Fact]
    public void ChecksumSet_Empty_AllNull()
    {
        Assert.Null(ChecksumSet.Empty.Crc32);
        Assert.Null(ChecksumSet.Empty.Crc32C);
        Assert.Null(ChecksumSet.Empty.Sha1);
        Assert.Null(ChecksumSet.Empty.Sha256);
    }
}
