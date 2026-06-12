using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Components;

namespace Vessel3.UI;

internal sealed class ObjectUrls(IAmazonS3 s3, UiConfig config, NavigationManager nav)
{
    public string For(string bucket, string key)
    {
        if (string.IsNullOrEmpty(config.AccessKey))
        {
            var origin = new Uri(nav.BaseUri).GetLeftPart(UriPartial.Authority);
            return $"{origin}/{Uri.EscapeDataString(bucket)}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
        }
        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddHours(1),
            Protocol = nav.BaseUri.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        });
    }
}
