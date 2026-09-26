using System.Globalization;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed class BucketXmlWriter : IBucketXmlWriter
{
    public async Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListAllMyBucketsResult", S3XmlDefaults.S3Namespace);

        await w.WriteStartElementAsync(null, "Owner", null);
        await w.WriteElementStringAsync(null, "ID", null, "vessel3");
        await w.WriteElementStringAsync(null, "DisplayName", null, "vessel3");
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(null, "Buckets", null);
        foreach (var b in buckets)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Bucket", null);
            await w.WriteElementStringAsync(null, "Name", null, b.Name);
            await w.WriteElementStringAsync(null, "CreationDate", null,
                b.CreatedAt.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteLocationConstraint(Stream output, string region, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "LocationConstraint", S3XmlDefaults.S3Namespace);
        if (region is not "us-east-1") await w.WriteStringAsync(region);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "VersioningConfiguration", S3XmlDefaults.S3Namespace);
        if (status is not VersioningStatus.Unversioned)
            await w.WriteElementStringAsync(null, "Status", null, status.ToString());
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteWebsiteConfiguration(Stream output, WebsiteConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "WebsiteConfiguration", S3XmlDefaults.S3Namespace);

        await w.WriteStartElementAsync(null, "IndexDocument", null);
        await w.WriteElementStringAsync(null, "Suffix", null, cfg.IndexDocument);
        await w.WriteEndElementAsync();

        if (!string.IsNullOrEmpty(cfg.ErrorDocument))
        {
            await w.WriteStartElementAsync(null, "ErrorDocument", null);
            await w.WriteElementStringAsync(null, "Key", null, cfg.ErrorDocument);
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCorsConfiguration(Stream output, CorsConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CORSConfiguration", S3XmlDefaults.S3Namespace);

        foreach (var rule in cfg.Rules)
        {
            await w.WriteStartElementAsync(null, "CORSRule", null);
            if (!string.IsNullOrEmpty(rule.Id))
            {
                await w.WriteElementStringAsync(null, "ID", null, rule.Id);
            }
            foreach (var origin in rule.AllowedOrigins)
            {
                await w.WriteElementStringAsync(null, "AllowedOrigin", null, origin);
            }
            foreach (var method in rule.AllowedMethods)
            {
                await w.WriteElementStringAsync(null, "AllowedMethod", null, method);
            }
            if (rule.AllowedHeaders is not null)
            {
                foreach (var header in rule.AllowedHeaders)
                {
                    await w.WriteElementStringAsync(null, "AllowedHeader", null, header);
                }
            }
            if (rule.MaxAgeSeconds.HasValue)
            {
                await w.WriteElementStringAsync(null, "MaxAgeSeconds", null, rule.MaxAgeSeconds.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (rule.ExposeHeaders is not null)
            {
                foreach (var expose in rule.ExposeHeaders)
                {
                    await w.WriteElementStringAsync(null, "ExposeHeader", null, expose);
                }
            }
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteAccessControlPolicy(Stream output, string ownerId, bool publicRead, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "AccessControlPolicy", S3XmlDefaults.S3Namespace);

        await w.WriteStartElementAsync(null, "Owner", null);
        await w.WriteElementStringAsync(null, "ID", null, ownerId);
        await w.WriteElementStringAsync(null, "DisplayName", null, ownerId);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(null, "AccessControlList", null);

        await WriteCanonicalUserGrant(w, ownerId, "FULL_CONTROL");
        if (publicRead)
        {
            await WriteAllUsersGroupGrant(w, "READ");
        }

        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteObjectLockConfiguration(Stream output, ObjectLockConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ObjectLockConfiguration", S3XmlDefaults.S3Namespace);
        if (cfg.Enabled)
            await w.WriteElementStringAsync(null, "ObjectLockEnabled", null, "Enabled");
        if (cfg.Default is { } def)
        {
            await w.WriteStartElementAsync(null, "Rule", null);
            await w.WriteStartElementAsync(null, "DefaultRetention", null);
            await w.WriteElementStringAsync(null, "Mode", null, S3XmlDefaults.ModeToWire(def.Mode));
            if (def.Days is { } d)
                await w.WriteElementStringAsync(null, "Days", null, d.ToString(CultureInfo.InvariantCulture));
            if (def.Years is { } y)
                await w.WriteElementStringAsync(null, "Years", null, y.ToString(CultureInfo.InvariantCulture));
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteLifecycleConfiguration(Stream output, LifecycleConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "LifecycleConfiguration", S3XmlDefaults.S3Namespace);
        foreach (var rule in cfg.Rules)
        {
            await w.WriteStartElementAsync(null, "Rule", null);
            if (!string.IsNullOrEmpty(rule.Id))
                await w.WriteElementStringAsync(null, "ID", null, rule.Id);
            await w.WriteStartElementAsync(null, "Filter", null);
            await w.WriteElementStringAsync(null, "Prefix", null, rule.Prefix);
            await w.WriteEndElementAsync();
            await w.WriteElementStringAsync(null, "Status", null, rule.Enabled ? "Enabled" : "Disabled");
            if (rule.ExpirationDays is not null || rule.ExpiredObjectDeleteMarker)
            {
                await w.WriteStartElementAsync(null, "Expiration", null);
                if (rule.ExpirationDays is { } d)
                    await w.WriteElementStringAsync(null, "Days", null, d.ToString(CultureInfo.InvariantCulture));
                if (rule.ExpiredObjectDeleteMarker)
                    await w.WriteElementStringAsync(null, "ExpiredObjectDeleteMarker", null, "true");
                await w.WriteEndElementAsync();
            }
            if (rule.NoncurrentDays is { } nd)
            {
                await w.WriteStartElementAsync(null, "NoncurrentVersionExpiration", null);
                await w.WriteElementStringAsync(null, "NoncurrentDays", null, nd.ToString(CultureInfo.InvariantCulture));
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListMultipartUploadsResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "IsTruncated", null, "false");

        foreach (var u in uploads)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Upload", null);
            await w.WriteElementStringAsync(null, "Key", null, u.Key);
            await w.WriteElementStringAsync(null, "UploadId", null, u.UploadId);
            await w.WriteElementStringAsync(null, "Initiated", null,
                u.Initiated.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private static async Task WriteCanonicalUserGrant(XmlWriter w, string ownerId, string permission)
    {
        await w.WriteStartElementAsync(null, "Grant", null);
        await w.WriteStartElementAsync(null, "Grantee", null);
        await w.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        await w.WriteAttributeStringAsync("xsi", "type", null, "CanonicalUser");
        await w.WriteElementStringAsync(null, "ID", null, ownerId);
        await w.WriteElementStringAsync(null, "DisplayName", null, ownerId);
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(null, "Permission", null, permission);
        await w.WriteEndElementAsync();
    }

    private static async Task WriteAllUsersGroupGrant(XmlWriter w, string permission)
    {
        await w.WriteStartElementAsync(null, "Grant", null);
        await w.WriteStartElementAsync(null, "Grantee", null);
        await w.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        await w.WriteAttributeStringAsync("xsi", "type", null, "Group");
        await w.WriteElementStringAsync(null, "URI", null, "http://acs.amazonaws.com/groups/global/AllUsers");
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(null, "Permission", null, permission);
        await w.WriteEndElementAsync();
    }
}
