using System.Text;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public sealed class ChecksumAlgorithmsTests
{
    [Fact]
    public void ComputeAll_EmptyInput_MatchesKnownVectors()
    {
        var (crc32, crc32c, sha1, sha256) = ChecksumAlgorithms.ComputeAll(ReadOnlySpan<byte>.Empty);

        Assert.Equal("00000000", crc32);
        Assert.Equal("00000000", crc32c);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", sha1);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha256);
    }

    [Fact]
    public void ComputeAll_Check123456789_MatchesKnownCrcVectors()
    {
        var data = Encoding.ASCII.GetBytes("123456789");
        var (crc32, crc32c, sha1, sha256) = ChecksumAlgorithms.ComputeAll(data);

        Assert.Equal("cbf43926", crc32);
        Assert.Equal("e3069283", crc32c);
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
        Assert.Equal(Vessel3.Server.S3.ChecksumHeaders.HeaderCrc32, Vessel3.Server.S3.ChecksumHeaders.HeaderFor(ChecksumAlgorithm.Crc32));
        Assert.Equal(Vessel3.Server.S3.ChecksumHeaders.HeaderCrc32C, Vessel3.Server.S3.ChecksumHeaders.HeaderFor(ChecksumAlgorithm.Crc32C));
        Assert.Equal(Vessel3.Server.S3.ChecksumHeaders.HeaderSha1, Vessel3.Server.S3.ChecksumHeaders.HeaderFor(ChecksumAlgorithm.Sha1));
        Assert.Equal(Vessel3.Server.S3.ChecksumHeaders.HeaderSha256, Vessel3.Server.S3.ChecksumHeaders.HeaderFor(ChecksumAlgorithm.Sha256));
    }

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
        var partHex = algo switch
        {
            ChecksumAlgorithm.Crc32 => crc32,
            ChecksumAlgorithm.Crc32C => crc32c,
            ChecksumAlgorithm.Sha1 => sha1,
            ChecksumAlgorithm.Sha256 => sha256,
            _ => throw new InvalidOperationException(),
        };

        var composite = ChecksumAlgorithms.Composite(algo, new[] { partHex });

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

    [Fact]
    public void ComputeAll_MismatchDetected()
    {
        var (_, _, _, sha256) = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("the real payload"));
        var declaredWrong = ChecksumAlgorithms.ComputeAll(Encoding.ASCII.GetBytes("a different payload")).Sha256;

        Assert.NotEqual(declaredWrong, sha256);

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

    [Fact]
    public void Crc32C_StreamingChunks_MatchesAllAtOnce()
    {
        var data = Encoding.ASCII.GetBytes("123456789");
        var c = new Crc32C();
        c.Append(data.AsSpan(0, 4));
        c.Append(data.AsSpan(4, 5));
        var hex = ChecksumAlgorithms.CrcUInt32ToHex(c.GetCurrentHashAndReset());

        Assert.Equal("e3069283", hex);
    }

    [Fact]
    public void Crc32C_LargeBuffer_MatchesDeterministicHash()
    {
        var buffer = new byte[65536];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i * 31 & 0xFF);
        }

        var directHash = Crc32C.HashToUInt32(buffer);

        var streaming = new Crc32C();
        for (var offset = 0; offset < buffer.Length; offset += 1024)
        {
            streaming.Append(buffer.AsSpan(offset, 1024));
        }
        var streamHash = streaming.GetCurrentHashAndReset();

        Assert.Equal(directHash, streamHash);
        Assert.NotEqual(0u, directHash);
    }

    [Fact]
    public void DeclaredChecksums_ImplicitConversion_PreservesValues()
    {
        var set = new ChecksumSet("c32", "c32c", null, "sha256");
        DeclaredChecksums declared = set;

        Assert.True(declared.HasAny);
        Assert.True(declared.Crc32.IsProvided);
        Assert.Equal("c32", declared.Crc32.Value);
        Assert.True(declared.Crc32C.IsProvided);
        Assert.Equal("c32c", declared.Crc32C.Value);
        Assert.False(declared.Sha1.HasExpectation);
        Assert.True(declared.Sha256.IsProvided);
        Assert.Equal("sha256", declared.Sha256.Value);
    }

    [Fact]
    public void ChecksumValidator_ValidatesMatchingBlob_AndRejectsMismatch()
    {
        var blob = new StoredBlob(
            Sha: "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225",
            Md5: "d41d8cd98f00b204e9800998ecf8427e",
            Crc32: "cbf43926",
            Crc32C: "e3069283",
            Sha1: "f7c3bc1d808e04732adf679965ccc34ca7ae3441",
            Size: 9);

        var declared = new DeclaredChecksums(
            ChecksumTarget.Provided("cbf43926"),
            ChecksumTarget.Provided("e3069283"),
            ChecksumTarget.None,
            ChecksumTarget.None);

        using var body = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        var ok = ChecksumValidator.Validate(blob, declared, body, out var toStore, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal("cbf43926", toStore.Crc32);
        Assert.Equal("e3069283", toStore.Crc32C);
        Assert.Null(toStore.Sha1);

        var mismatch = new DeclaredChecksums(
            ChecksumTarget.Provided("00000000"),
            ChecksumTarget.None,
            ChecksumTarget.None,
            ChecksumTarget.None);

        using var mismatchBody = new MemoryStream();
        var failed = ChecksumValidator.Validate(blob, mismatch, mismatchBody, out _, out var mismatchErr);

        Assert.False(failed);
        Assert.IsType<BadDigestError>(mismatchErr);
    }
}
