using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class S3SubresourceResolverTests
{
    private readonly S3SubresourceResolver resolver = new();

    [Fact]
    public void Empty_Query_Returns_None()
    {
        var query = new QueryCollection();
        Assert.Equal(S3BucketSubresource.None, resolver.ResolveBucket(query));
        Assert.Equal(S3KeySubresource.None, resolver.ResolveKey(query));
    }

    [Theory]
    [InlineData("location", "Location")]
    [InlineData("uploads", "Uploads")]
    [InlineData("versioning", "Versioning")]
    [InlineData("object-lock", "ObjectLock")]
    [InlineData("lifecycle", "Lifecycle")]
    [InlineData("versions", "Versions")]
    [InlineData("delete", "Delete")]
    [InlineData("website", "Website")]
    [InlineData("cors", "Cors")]
    [InlineData("acl", "Acl")]
    public void Resolves_Bucket_Subresources(string param, string expected)
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            [param] = StringValues.Empty
        });

        Assert.Equal(expected, resolver.ResolveBucket(query).ToString());
    }

    [Theory]
    [InlineData("uploads", "1", "Uploads")]
    [InlineData("uploadId", "abc-123", "UploadId")]
    [InlineData("tagging", "", "Tagging")]
    [InlineData("retention", "", "Retention")]
    [InlineData("legal-hold", "", "LegalHold")]
    [InlineData("attributes", "", "Attributes")]
    public void Resolves_Key_Subresources(string param, string val, string expected)
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            [param] = new StringValues(val)
        });

        Assert.Equal(expected, resolver.ResolveKey(query).ToString());
    }

    [Fact]
    public void UploadId_With_Empty_Value_Returns_None()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["uploadId"] = StringValues.Empty
        });

        Assert.Equal(S3KeySubresource.None, resolver.ResolveKey(query));
    }

    [Fact]
    public void Resolves_Bucket_Priority()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["acl"] = StringValues.Empty,
            ["location"] = StringValues.Empty
        });

        Assert.Equal(S3BucketSubresource.Location, resolver.ResolveBucket(query));
    }
}
