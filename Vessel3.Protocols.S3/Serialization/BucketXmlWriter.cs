using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

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

    public Task WriteLocationConstraint(Stream output, string region, CancellationToken ct)
    {
        var content = region is "us-east-1" ? "" : SecurityElement.Escape(region);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><LocationConstraint xmlns="{S3XmlDefaults.S3Namespace}">{content}</LocationConstraint>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct)
    {
        var body = status is not VersioningStatus.Unversioned
            ? $"<Status>{status}</Status>"
            : "";
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><VersioningConfiguration xmlns="{S3XmlDefaults.S3Namespace}">{body}</VersioningConfiguration>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteWebsiteConfiguration(Stream output, WebsiteConfig cfg, CancellationToken ct)
    {
        var errorDoc = !string.IsNullOrEmpty(cfg.ErrorDocument)
            ? $"<ErrorDocument><Key>{SecurityElement.Escape(cfg.ErrorDocument)}</Key></ErrorDocument>"
            : "";
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><WebsiteConfiguration xmlns="{S3XmlDefaults.S3Namespace}"><IndexDocument><Suffix>{SecurityElement.Escape(cfg.IndexDocument)}</Suffix></IndexDocument>{errorDoc}</WebsiteConfiguration>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
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

    public Task WriteAccessControlPolicy(Stream output, string ownerId, bool publicRead, CancellationToken ct)
    {
        var escapedOwner = SecurityElement.Escape(ownerId);
        var publicGrant = publicRead
            ? """<Grant><Grantee xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="Group"><URI>http://acs.amazonaws.com/groups/global/AllUsers</URI></Grantee><Permission>READ</Permission></Grant>"""
            : "";
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><AccessControlPolicy xmlns="{S3XmlDefaults.S3Namespace}"><Owner><ID>{escapedOwner}</ID><DisplayName>{escapedOwner}</DisplayName></Owner><AccessControlList><Grant><Grantee xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="CanonicalUser"><ID>{escapedOwner}</ID><DisplayName>{escapedOwner}</DisplayName></Grantee><Permission>FULL_CONTROL</Permission></Grant>{publicGrant}</AccessControlList></AccessControlPolicy>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteObjectLockConfiguration(Stream output, ObjectLockConfig cfg, CancellationToken ct)
    {
        var enabledXml = cfg.Enabled ? "<ObjectLockEnabled>Enabled</ObjectLockEnabled>" : "";
        var ruleXml = "";
        if (cfg.Default is { } def)
        {
            var mode = S3XmlDefaults.ModeToWire(def.Mode);
            var period = def.Days is { } d
                ? $"<Days>{d.ToString(CultureInfo.InvariantCulture)}</Days>"
                : (def.Years is { } y ? $"<Years>{y.ToString(CultureInfo.InvariantCulture)}</Years>" : "");
            ruleXml = $"<Rule><DefaultRetention><Mode>{mode}</Mode>{period}</DefaultRetention></Rule>";
        }
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><ObjectLockConfiguration xmlns="{S3XmlDefaults.S3Namespace}">{enabledXml}{ruleXml}</ObjectLockConfiguration>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
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
}
