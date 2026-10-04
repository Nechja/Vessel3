using System.Globalization;
using System.Xml;

namespace Vessel3.Server.S3;

internal sealed class BucketXmlReader : IBucketXmlReader
{
    public async Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? currentField = null;
            string? statusValue = null;
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        currentField = reader.LocalName;
                        break;
                    case XmlNodeType.Text:
                        if (currentField is "Status") statusValue = await reader.GetValueAsync();
                        break;
                    case XmlNodeType.EndElement:
                        currentField = null;
                        break;
                }
            }
            return statusValue is null
                ? new MalformedXmlError("VersioningConfiguration missing Status element")
                : Enum.TryParse<VersioningStatus>(statusValue, out var parsedStatus) && parsedStatus is not VersioningStatus.Unversioned
                    ? parsedStatus
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? enabled = null;
            string? mode = null;
            int? days = null;
            int? years = null;
            string? current = null;
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        current = reader.LocalName;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        switch (current)
                        {
                            case "ObjectLockEnabled": enabled = await reader.GetValueAsync(); break;
                            case "Mode": mode = await reader.GetValueAsync(); break;
                            case "Days":
                                if (int.TryParse(await reader.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var daysValue)) days = daysValue;
                                break;
                            case "Years":
                                if (int.TryParse(await reader.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearsValue)) years = yearsValue;
                                break;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
                }
            }
            var isEnabled = enabled is "Enabled";
            ObjectLockDefault? defaultRetention = null;
            if (mode is not null)
            {
                if (!S3XmlDefaults.TryParseMode(mode, out var retentionMode))
                    return new MalformedXmlError($"unknown Mode '{mode}'");
                if (days is null && years is null)
                    return new MalformedXmlError("DefaultRetention requires Days or Years");
                defaultRetention = new ObjectLockDefault(retentionMode, days, years);
            }
            return new ObjectLockConfig(isEnabled, defaultRetention);
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is not XmlNodeType.Element || reader.LocalName is not "Rule") continue;
                if (!(await ReadLifecycleRule(reader)).TryGetValue(out var rule, out var ruleError)) return ruleError;
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

    private static async Task<Result<LifecycleRule>> ReadLifecycleRule(XmlReader reader)
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

        using var subtree = reader.ReadSubtree();
        while (await subtree.ReadAsync())
        {
            switch (subtree.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = subtree.LocalName;
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
                        case "ID": id = await subtree.GetValueAsync(); break;
                        case "Status": status = await subtree.GetValueAsync(); break;
                        case "Prefix": prefix = await subtree.GetValueAsync(); break;
                        case "Days" when section is "Expiration":
                            if (int.TryParse(await subtree.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var daysValue)) days = daysValue;
                            break;
                        case "NoncurrentDays" when section is "NoncurrentVersionExpiration":
                            if (int.TryParse(await subtree.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var noncurrentDaysValue)) noncurrentDays = noncurrentDaysValue;
                            break;
                        case "ExpiredObjectDeleteMarker":
                            expiredMarker = (await subtree.GetValueAsync()).Equals("true", StringComparison.OrdinalIgnoreCase);
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
        int? noncurrentDays)
    {
        if (sawTransition)
        {
            return new InvalidArgumentError("Transitions are not supported");
        }

        if (sawAbortMultipart)
        {
            return new InvalidArgumentError("AbortIncompleteMultipartUpload is not supported in this version");
        }

        if (sawFilterTag)
        {
            return new InvalidArgumentError("Tag filters are not supported in this version");
        }

        if (sawFilterAnd)
        {
            return new InvalidArgumentError("Compound Filter (And) is not supported in this version");
        }

        if (status is not ("Enabled" or "Disabled"))
        {
            return new MalformedXmlError($"Rule Status must be Enabled or Disabled, got '{status}'");
        }

        if (!sawExpiration && !sawNoncurrent)
        {
            return new MalformedXmlError("Rule requires an Expiration or NoncurrentVersionExpiration element");
        }

        if (sawExpiration && days is null && !expiredMarker)
        {
            return new MalformedXmlError("Expiration requires Days or ExpiredObjectDeleteMarker");
        }

        if (days is { } daysValue && daysValue < 1)
        {
            return new InvalidArgumentError("Expiration.Days must be >= 1");
        }

        if (sawNoncurrent && noncurrentDays is null)
        {
            return new MalformedXmlError("NoncurrentVersionExpiration requires NoncurrentDays");
        }

        if (noncurrentDays is { } noncurrentDaysValue && noncurrentDaysValue < 1)
        {
            return new InvalidArgumentError("NoncurrentVersionExpiration.NoncurrentDays must be >= 1");
        }

        return null;
    }

    public async Task<Result<WebsiteConfig>> ReadWebsiteConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? indexSuffix = null;
            string? errorKey = null;
            string? current = null;
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        current = reader.LocalName;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        switch (current)
                        {
                            case "Suffix": indexSuffix = (await reader.GetValueAsync()).Trim(); break;
                            case "Key": errorKey = (await reader.GetValueAsync()).Trim(); break;
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            List<CorsRule> rules = [];
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is not XmlNodeType.Element || reader.LocalName is not "CORSRule") continue;
                var ruleResult = await ReadCorsRule(reader, ct);
                if (!ruleResult.TryGetValue(out var rule, out var ruleError)) return ruleError;
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

    private static async Task<Result<CorsRule>> ReadCorsRule(XmlReader reader, CancellationToken ct)
    {
        string? id = null;
        List<string> origins = [];
        List<string> methods = [];
        List<string> headers = [];
        List<string> exposeHeaders = [];
        int? maxAgeSeconds = null;

        using var subtree = reader.ReadSubtree();
        string? currentField = null;

        while (await subtree.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            switch (subtree.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = subtree.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    var elementValue = (await subtree.GetValueAsync()).Trim();
                    switch (currentField)
                    {
                        case "ID": id = elementValue; break;
                        case "AllowedOrigin": if (!string.IsNullOrEmpty(elementValue)) origins.Add(elementValue); break;
                        case "AllowedMethod": if (!string.IsNullOrEmpty(elementValue)) methods.Add(elementValue.ToUpperInvariant()); break;
                        case "AllowedHeader": if (!string.IsNullOrEmpty(elementValue)) headers.Add(elementValue); break;
                        case "ExposeHeader": if (!string.IsNullOrEmpty(elementValue)) exposeHeaders.Add(elementValue); break;
                        case "MaxAgeSeconds":
                            if (int.TryParse(elementValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge))
                                maxAgeSeconds = maxAge;
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            var publicRead = false;
            string? currentField = null;
            var isGroupGrantee = false;
            var isAllUsersUri = false;

            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        currentField = reader.LocalName;
                        if (currentField is "Grantee")
                        {
                            isGroupGrantee = reader.GetAttribute("type") is "Group" || reader.GetAttribute("xsi:type") is "Group";
                            isAllUsersUri = false;
                        }
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        var elementValue = (await reader.GetValueAsync()).Trim();
                        if (currentField is "URI" && isGroupGrantee && elementValue.Contains("AllUsers", StringComparison.OrdinalIgnoreCase))
                        {
                            isAllUsersUri = true;
                        }
                        else if (currentField is "Permission" && isAllUsersUri && elementValue is "READ" or "FULL_CONTROL")
                        {
                            publicRead = true;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        if (reader.LocalName is "Grant")
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
