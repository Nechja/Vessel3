using Microsoft.AspNetCore.Components;

namespace Vessel3.UI;

public sealed class ObjectUrls(NavigationManager nav)
{
    public string For(string bucket, string key, string? versionId = null)
    {
        var origin = new Uri(nav.BaseUri).GetLeftPart(UriPartial.Authority);
        var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var url = $"{origin}/v1/buckets/{Uri.EscapeDataString(bucket)}/objects/{escapedKey}";
        return versionId is null ? url : $"{url}?versionId={Uri.EscapeDataString(versionId)}";
    }
}
