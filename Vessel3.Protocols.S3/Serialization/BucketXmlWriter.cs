using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using Vessel3.IO;

namespace Vessel3.Server.S3;

internal sealed class BucketXmlWriter : IBucketXmlWriter
{
    public async Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct)
    {
        var bucketList = buckets as IReadOnlyCollection<BucketInfo> ?? [.. buckets];
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, S3XmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "ListAllMyBucketsResult", S3XmlDefaults.S3Namespace);

            w.WriteStartElement(null, "Owner", null);
            w.WriteElementString(null, "ID", null, "vessel3");
            w.WriteElementString(null, "DisplayName", null, "vessel3");
            w.WriteEndElement();

            w.WriteStartElement(null, "Buckets", null);
            foreach (var b in bucketList)
            {
                ct.ThrowIfCancellationRequested();
                w.WriteStartElement(null, "Bucket", null);
                w.WriteElementString(null, "Name", null, b.Name);
                w.WriteElementString(null, "CreationDate", null,
                    b.CreatedAt.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
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
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, S3XmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "CORSConfiguration", S3XmlDefaults.S3Namespace);

            foreach (var rule in cfg.Rules)
            {
                w.WriteStartElement(null, "CORSRule", null);
                if (!string.IsNullOrEmpty(rule.Id))
                {
                    w.WriteElementString(null, "ID", null, rule.Id);
                }
                foreach (var origin in rule.AllowedOrigins)
                {
                    w.WriteElementString(null, "AllowedOrigin", null, origin);
                }
                foreach (var method in rule.AllowedMethods)
                {
                    w.WriteElementString(null, "AllowedMethod", null, method);
                }
                if (rule.AllowedHeaders is not null)
                {
                    foreach (var header in rule.AllowedHeaders)
                    {
                        w.WriteElementString(null, "AllowedHeader", null, header);
                    }
                }
                if (rule.MaxAgeSeconds.HasValue)
                {
                    w.WriteElementString(null, "MaxAgeSeconds", null, rule.MaxAgeSeconds.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (rule.ExposeHeaders is not null)
                {
                    foreach (var expose in rule.ExposeHeaders)
                    {
                        w.WriteElementString(null, "ExposeHeader", null, expose);
                    }
                }
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
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
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, S3XmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "LifecycleConfiguration", S3XmlDefaults.S3Namespace);
            foreach (var rule in cfg.Rules)
            {
                w.WriteStartElement(null, "Rule", null);
                if (!string.IsNullOrEmpty(rule.Id))
                    w.WriteElementString(null, "ID", null, rule.Id);
                w.WriteStartElement(null, "Filter", null);
                w.WriteElementString(null, "Prefix", null, rule.Prefix);
                w.WriteEndElement();
                w.WriteElementString(null, "Status", null, rule.Enabled ? "Enabled" : "Disabled");
                if (rule.ExpirationDays is not null || rule.ExpiredObjectDeleteMarker)
                {
                    w.WriteStartElement(null, "Expiration", null);
                    if (rule.ExpirationDays is { } d)
                        w.WriteElementString(null, "Days", null, d.ToString(CultureInfo.InvariantCulture));
                    if (rule.ExpiredObjectDeleteMarker)
                        w.WriteElementString(null, "ExpiredObjectDeleteMarker", null, "true");
                    w.WriteEndElement();
                }
                if (rule.NoncurrentDays is { } nd)
                {
                    w.WriteStartElement(null, "NoncurrentVersionExpiration", null);
                    w.WriteElementString(null, "NoncurrentDays", null, nd.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
    }

    public async Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct)
    {
        var uploadList = uploads as IReadOnlyCollection<InProgressUpload> ?? [.. uploads];
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, S3XmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "ListMultipartUploadsResult", S3XmlDefaults.S3Namespace);
            w.WriteElementString(null, "Bucket", null, bucket);
            w.WriteElementString(null, "IsTruncated", null, "false");

            foreach (var u in uploadList)
            {
                ct.ThrowIfCancellationRequested();
                w.WriteStartElement(null, "Upload", null);
                w.WriteElementString(null, "Key", null, u.Key);
                w.WriteElementString(null, "UploadId", null, u.UploadId);
                w.WriteElementString(null, "Initiated", null,
                    u.Initiated.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
                w.WriteElementString(null, "StorageClass", null, "STANDARD");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
    }
}
