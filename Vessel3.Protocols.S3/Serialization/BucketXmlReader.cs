using System.Globalization;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed class BucketXmlReader : IBucketXmlReader
{
    public async Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? currentField = null;
            string? statusValue = null;
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        currentField = r.LocalName;
                        break;
                    case XmlNodeType.Text:
                        if (currentField is "Status") statusValue = await r.GetValueAsync();
                        break;
                    case XmlNodeType.EndElement:
                        currentField = null;
                        break;
                }
            }
            return statusValue is null
                ? new MalformedXmlError("VersioningConfiguration missing Status element")
                : Enum.TryParse<VersioningStatus>(statusValue, out var s) && s is not VersioningStatus.Unversioned
                    ? s
                    : new MalformedXmlError($"unknown Status '{statusValue}'");
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    public async Task<Result<ObjectLockConfig>> ReadObjectLockConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? enabled = null;
            string? mode = null;
            int? days = null;
            int? years = null;
            string? current = null;
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        current = r.LocalName;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        switch (current)
                        {
                            case "ObjectLockEnabled": enabled = await r.GetValueAsync(); break;
                            case "Mode": mode = await r.GetValueAsync(); break;
                            case "Days":
                                if (int.TryParse(await r.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) days = d;
                                break;
                            case "Years":
                                if (int.TryParse(await r.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) years = y;
                                break;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
                }
            }
            var en = enabled is "Enabled";
            ObjectLockDefault? def = null;
            if (mode is not null)
            {
                if (!S3XmlDefaults.TryParseMode(mode, out var rm))
                    return new MalformedXmlError($"unknown Mode '{mode}'");
                if (days is null && years is null)
                    return new MalformedXmlError("DefaultRetention requires Days or Years");
                def = new ObjectLockDefault(rm, days, years);
            }
            return new ObjectLockConfig(en, def);
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    public async Task<Result<LifecycleConfig>> ReadLifecycleConfiguration(Stream input, CancellationToken ct)
    {
        List<LifecycleRule> rules = [];
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element || r.LocalName is not "Rule") continue;
                if (!(await ReadLifecycleRule(r)).TryGetValue(out var rule, out var ruleErr)) return ruleErr;
                rules.Add(rule);
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
        return rules.Count is 0
            ? new MalformedXmlError("LifecycleConfiguration requires at least one Rule")
            : new LifecycleConfig(rules);
    }

    private static async Task<Result<LifecycleRule>> ReadLifecycleRule(XmlReader r)
    {
        string? id = null;
        string? status = null;
        string prefix = string.Empty;
        int? days = null;
        int? noncurrentDays = null;
        var expiredMarker = false;
        var sawExpiration = false;
        var sawTransition = false;
        var sawNoncurrent = false;
        var sawAbortMultipart = false;
        var sawFilterTag = false;
        var sawFilterAnd = false;
        string? currentField = null;
        var depth = 0;
        var section = "";

        using var sub = r.ReadSubtree();
        while (await sub.ReadAsync())
        {
            switch (sub.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = sub.LocalName;
                    if (currentField is "Expiration") { sawExpiration = true; section = "Expiration"; }
                    else if (currentField is "Transition" or "NoncurrentVersionTransition") sawTransition = true;
                    else if (currentField is "NoncurrentVersionExpiration") { sawNoncurrent = true; section = "NoncurrentVersionExpiration"; }
                    else if (currentField is "AbortIncompleteMultipartUpload") sawAbortMultipart = true;
                    else if (currentField is "Filter") section = "Filter";
                    else if (section is "Filter" && currentField is "Tag") sawFilterTag = true;
                    else if (section is "Filter" && currentField is "And") sawFilterAnd = true;
                    depth++;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    switch (currentField)
                    {
                        case "ID": id = await sub.GetValueAsync(); break;
                        case "Status": status = await sub.GetValueAsync(); break;
                        case "Prefix": prefix = await sub.GetValueAsync(); break;
                        case "Days" when section is "Expiration":
                            if (int.TryParse(await sub.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) days = d;
                            break;
                        case "NoncurrentDays" when section is "NoncurrentVersionExpiration":
                            if (int.TryParse(await sub.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nd)) noncurrentDays = nd;
                            break;
                        case "ExpiredObjectDeleteMarker":
                            expiredMarker = (await sub.GetValueAsync()).Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    depth--;
                    if (depth <= 0) section = "";
                    break;
            }
        }

        var validationError = ValidateLifecycleRule(
            sawTransition,
            sawAbortMultipart,
            sawFilterTag,
            sawFilterAnd,
            status,
            sawExpiration,
            sawNoncurrent,
            days,
            expiredMarker,
            noncurrentDays);
        return validationError is not null
            ? validationError
            : new LifecycleRule(id ?? string.Empty, status is "Enabled", prefix, days, expiredMarker, noncurrentDays);
    }

    private static Error? ValidateLifecycleRule(
        bool sawTransition,
        bool sawAbortMultipart,
        bool sawFilterTag,
        bool sawFilterAnd,
        string? status,
        bool sawExpiration,
        bool sawNoncurrent,
        int? days,
        bool expiredMarker,
        int? noncurrentDays) =>
        sawTransition ? new InvalidArgumentError("Transitions are not supported")
        : sawAbortMultipart ? new InvalidArgumentError("AbortIncompleteMultipartUpload is not supported in this version")
        : sawFilterTag ? new InvalidArgumentError("Tag filters are not supported in this version")
        : sawFilterAnd ? new InvalidArgumentError("Compound Filter (And) is not supported in this version")
        : status is not ("Enabled" or "Disabled") ? new MalformedXmlError($"Rule Status must be Enabled or Disabled, got '{status}'")
        : !sawExpiration && !sawNoncurrent ? new MalformedXmlError("Rule requires an Expiration or NoncurrentVersionExpiration element")
        : sawExpiration && days is null && !expiredMarker ? new MalformedXmlError("Expiration requires Days or ExpiredObjectDeleteMarker")
        : days is { } dd && dd < 1 ? new InvalidArgumentError("Expiration.Days must be >= 1")
        : sawNoncurrent && noncurrentDays is null ? new MalformedXmlError("NoncurrentVersionExpiration requires NoncurrentDays")
        : noncurrentDays is { } ndd && ndd < 1 ? new InvalidArgumentError("NoncurrentVersionExpiration.NoncurrentDays must be >= 1")
        : null;

    public async Task<Result<WebsiteConfig>> ReadWebsiteConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? indexSuffix = null;
            string? errorKey = null;
            string? current = null;
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        current = r.LocalName;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        switch (current)
                        {
                            case "Suffix": indexSuffix = (await r.GetValueAsync()).Trim(); break;
                            case "Key": errorKey = (await r.GetValueAsync()).Trim(); break;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
                }
            }

            return string.IsNullOrEmpty(indexSuffix)
                ? new MalformedXmlError("WebsiteConfiguration requires a non-empty IndexDocument Suffix")
                : new WebsiteConfig(indexSuffix, string.IsNullOrEmpty(errorKey) ? null : errorKey);
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    public async Task<Result<CorsConfig>> ReadCorsConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            List<CorsRule> rules = [];
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element || r.LocalName is not "CORSRule") continue;
                var ruleResult = await ReadCorsRule(r, ct);
                if (!ruleResult.TryGetValue(out var rule, out var ruleErr)) return ruleErr;
                rules.Add(rule);
            }

            return rules.Count switch
            {
                0 => new MalformedXmlError("CORSConfiguration must contain at least one CORSRule"),
                > 100 => new MalformedXmlError("CORSConfiguration cannot contain more than 100 rules"),
                _ => new CorsConfig(rules)
            };
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    private static async Task<Result<CorsRule>> ReadCorsRule(XmlReader r, CancellationToken ct)
    {
        string? id = null;
        List<string> origins = [];
        List<string> methods = [];
        List<string> headers = [];
        List<string> exposeHeaders = [];
        int? maxAgeSeconds = null;

        using var sub = r.ReadSubtree();
        string? currentField = null;

        while (await sub.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            switch (sub.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = sub.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    var val = (await sub.GetValueAsync()).Trim();
                    switch (currentField)
                    {
                        case "ID": id = val; break;
                        case "AllowedOrigin": if (!string.IsNullOrEmpty(val)) origins.Add(val); break;
                        case "AllowedMethod": if (!string.IsNullOrEmpty(val)) methods.Add(val.ToUpperInvariant()); break;
                        case "AllowedHeader": if (!string.IsNullOrEmpty(val)) headers.Add(val); break;
                        case "ExposeHeader": if (!string.IsNullOrEmpty(val)) exposeHeaders.Add(val); break;
                        case "MaxAgeSeconds":
                            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
                                maxAgeSeconds = age;
                            break;
                    }
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }

        return origins.Count is 0
            ? new MalformedXmlError("CORSRule requires at least one AllowedOrigin")
            : methods.Count is 0
                ? new MalformedXmlError("CORSRule requires at least one AllowedMethod")
                : new CorsRule(origins, methods, headers, exposeHeaders, maxAgeSeconds, id);
    }

    public async Task<Result<bool>> ReadAccessControlPolicy(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            var publicRead = false;
            string? currentField = null;
            var isGroupGrantee = false;
            var isAllUsersUri = false;

            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        currentField = r.LocalName;
                        if (currentField is "Grantee")
                        {
                            isGroupGrantee = r.GetAttribute("type") is "Group" || r.GetAttribute("xsi:type") is "Group";
                            isAllUsersUri = false;
                        }
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        var val = (await r.GetValueAsync()).Trim();
                        if (currentField is "URI" && isGroupGrantee && val.Contains("AllUsers", StringComparison.OrdinalIgnoreCase))
                        {
                            isAllUsersUri = true;
                        }
                        else if (currentField is "Permission" && isAllUsersUri && val is "READ" or "FULL_CONTROL")
                        {
                            publicRead = true;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        if (r.LocalName is "Grant")
                        {
                            isGroupGrantee = false;
                            isAllUsersUri = false;
                        }
                        currentField = null;
                        break;
                }
            }

            return publicRead;
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }
}
