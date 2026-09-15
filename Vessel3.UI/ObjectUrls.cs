using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Components;

namespace Vessel3.UI;

internal sealed class ObjectUrls(IAmazonS3 s3, UiAuth auth, NavigationManager nav)
{
    public string For(string bucket, string key) => Url(bucket, key, HttpVerb.GET);

    public string ForUpload(string bucket, string key) => Url(bucket, key, HttpVerb.PUT);

    private string Url(string bucket, string key, HttpVerb verb)
    {
        if (auth.Anonymous)
        {
            var origin = new Uri(nav.BaseUri).GetLeftPart(UriPartial.Authority);
            return $"{origin}/{Uri.EscapeDataString(bucket)}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
        }
        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = verb,
            Expires = DateTime.UtcNow.AddHours(1),
            Protocol = nav.BaseUri.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        });
    }
}
