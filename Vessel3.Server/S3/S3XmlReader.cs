using System.Globalization;
using System.Xml;
using Vessel3.Server;
using Vessel3.Server.Storage;

namespace Vessel3.Server.S3;

internal sealed record BatchDeleteKey(string Key, string? VersionId);
internal sealed record BatchDeleteRequest(IReadOnlyList<BatchDeleteKey> Keys, bool Quiet);
internal sealed record BatchDeleteOutcome(string Key, string? VersionId, Error? Error);
internal sealed record CompletedPart(int Number, string Etag, CompletedPartChecksums? Sums = null);

internal interface IS3XmlReader
{
    Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct);
    Task<Result<IReadOnlyList<CompletedPart>>> ReadCompleteMultipartUploadRequest(Stream input, CancellationToken ct);
    Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct);
    Task<Result<IReadOnlyDictionary<string, string>>> ReadTagging(Stream input, CancellationToken ct);
    Task<Result<ObjectLockConfig>> ReadObjectLockConfiguration(Stream input, CancellationToken ct);
    Task<Result<LifecycleConfig>> ReadLifecycleConfiguration(Stream input, CancellationToken ct);
    Task<Result<Retention>> ReadRetention(Stream input, CancellationToken ct);
    Task<Result<bool>> ReadLegalHold(Stream input, CancellationToken ct);
}

internal sealed class S3XmlReader : IS3XmlReader
{
    private readonly XmlReaderSettings settings = new()
    {
        Async = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    public async Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct)
    {
        var keys = new List<BatchDeleteKey>();
        var quiet = false;

        try
        {
            using var r = XmlReader.Create(input, settings);
            var advanced = true;
            while (advanced)
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element) { advanced = await r.ReadAsync(); continue; }

                if (r.LocalName is "Object")
                {
                    var key = await ReadObjectEntry(r);
                    if (key is not null) keys.Add(key);
                    advanced = await r.ReadAsync();
                }
                else if (r.LocalName is "Quiet")
                {
                    var raw = await r.ReadElementContentAsStringAsync();
                    quiet = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                    advanced = !r.EOF;
                }
                else
                {
                    advanced = await r.ReadAsync();
                }
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }

        return new BatchDeleteRequest(keys, quiet);
    }

    public async Task<Result<IReadOnlyList<CompletedPart>>> ReadCompleteMultipartUploadRequest(Stream input, CancellationToken ct)
    {
        var parts = new List<CompletedPart>();

        try
        {
            using var r = XmlReader.Create(input, settings);
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element) continue;
                if (r.LocalName is not "Part") continue;

                var part = await ReadPartEntry(r);
                if (part is not null) parts.Add(part);
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }

        return (Result<IReadOnlyList<CompletedPart>>)parts;
    }

    public async Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, settings);
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
                        if (currentField is "Status") statusValue = r.Value;
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
                    : (Result<VersioningStatus>)new MalformedXmlError($"unknown Status '{statusValue}'");
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyDictionary<string, string>>> ReadTagging(Stream input, CancellationToken ct)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        try
        {
            using var r = XmlReader.Create(input, settings);
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element || r.LocalName is not "Tag") continue;
                var (k, v) = await ReadTagEntry(r);
                if (k is null) continue;
                pairs.Add(new KeyValuePair<string, string>(k, v ?? string.Empty));
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
        return TagSet.Validate(pairs);
    }

    private static async Task<(string? Key, string? Value)> ReadTagEntry(XmlReader r)
    {
        string? key = null;
        string? value = null;
        string? currentField = null;
        using var sub = r.ReadSubtree();
        while (await sub.ReadAsync())
        {
            switch (sub.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = sub.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    if (currentField is "Key") key = sub.Value;
                    else if (currentField is "Value") value = sub.Value;
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }
        return (key, value);
    }

    private static async Task<CompletedPart?> ReadPartEntry(XmlReader r)
    {
        int? number = null;
        string? etag = null;
        string? c32 = null, c32c = null, s1 = null, s256 = null;
        string? currentField = null;
        using var sub = r.ReadSubtree();
        while (await sub.ReadAsync())
        {
            switch (sub.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = sub.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    switch (currentField)
                    {
                        case "PartNumber":
                            if (int.TryParse(sub.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) number = n;
                            break;
                        case "ETag": etag = sub.Value.Trim('"'); break;
                        case "ChecksumCRC32":   c32 = ChecksumAlgorithms.Base64ToHex(sub.Value); break;
                        case "ChecksumCRC32C":  c32c = ChecksumAlgorithms.Base64ToHex(sub.Value); break;
                        case "ChecksumSHA1":    s1 = ChecksumAlgorithms.Base64ToHex(sub.Value); break;
                        case "ChecksumSHA256":  s256 = ChecksumAlgorithms.Base64ToHex(sub.Value); break;
                    }
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }
        if (number is not { } n2 || etag is null) return null;
        CompletedPartChecksums? sums = (c32 ?? c32c ?? s1 ?? s256) is null ? null : new CompletedPartChecksums(c32, c32c, s1, s256);
        return new CompletedPart(n2, etag, sums);
    }

    public async Task<Result<ObjectLockConfig>> ReadObjectLockConfiguration(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, settings);
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
                            case "ObjectLockEnabled": enabled = r.Value; break;
                            case "Mode": mode = r.Value; break;
                            case "Days":
                                if (int.TryParse(r.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) days = d;
                                break;
                            case "Years":
                                if (int.TryParse(r.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) years = y;
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
                if (!TryParseMode(mode, out var rm))
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
        var rules = new List<LifecycleRule>();
        try
        {
            using var r = XmlReader.Create(input, settings);
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element || r.LocalName is not "Rule") continue;
                var parsed = await ReadLifecycleRule(r);
                if (parsed is Result<LifecycleRule>.Failure f) return f.Error;
                rules.Add(((Result<LifecycleRule>.Success)parsed).Value);
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
        return rules.Count is 0
            ? new MalformedXmlError("LifecycleConfiguration requires at least one Rule")
            : (Result<LifecycleConfig>)new LifecycleConfig(rules);
    }

    private static async Task<Result<LifecycleRule>> ReadLifecycleRule(XmlReader r)
    {
        string? id = null;
        string? status = null;
        string prefix = string.Empty;
        int? days = null;
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
                    else if (currentField is "NoncurrentVersionExpiration") sawNoncurrent = true;
                    else if (currentField is "AbortIncompleteMultipartUpload") sawAbortMultipart = true;
                    else if (currentField is "Filter") section = "Filter";
                    else if (section is "Filter" && currentField is "Tag") sawFilterTag = true;
                    else if (section is "Filter" && currentField is "And") sawFilterAnd = true;
                    depth++;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    switch (currentField)
                    {
                        case "ID": id = sub.Value; break;
                        case "Status": status = sub.Value; break;
                        case "Prefix": prefix = sub.Value; break;
                        case "Days" when section is "Expiration":
                            if (int.TryParse(sub.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) days = d;
                            break;
                        case "ExpiredObjectDeleteMarker":
                            expiredMarker = sub.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
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

        Error? err =
            sawTransition ? new InvalidArgumentError("Transitions are not supported")
            : sawNoncurrent ? new InvalidArgumentError("NoncurrentVersionExpiration is not supported in this version")
            : sawAbortMultipart ? new InvalidArgumentError("AbortIncompleteMultipartUpload is not supported in this version")
            : sawFilterTag ? new InvalidArgumentError("Tag filters are not supported in this version")
            : sawFilterAnd ? new InvalidArgumentError("Compound Filter (And) is not supported in this version")
            : status is not ("Enabled" or "Disabled") ? new MalformedXmlError($"Rule Status must be Enabled or Disabled, got '{status}'")
            : !sawExpiration ? new MalformedXmlError("Rule requires an Expiration element")
            : days is null && !expiredMarker ? new MalformedXmlError("Expiration requires Days or ExpiredObjectDeleteMarker")
            : days is { } dd && dd < 1 ? new InvalidArgumentError("Expiration.Days must be >= 1")
            : (Error?)null;
        return err is not null
            ? err
            : (Result<LifecycleRule>)new LifecycleRule(id ?? string.Empty, status is "Enabled", prefix, days, expiredMarker);
    }

    public async Task<Result<Retention>> ReadRetention(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, settings);
            string? mode = null;
            string? until = null;
            string? current = null;
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element: current = r.LocalName; break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        if (current is "Mode") mode = r.Value;
                        else if (current is "RetainUntilDate") until = r.Value;
                        break;
                    case XmlNodeType.EndElement: current = null; break;
                }
            }
            return mode is null || until is null
                ? new MalformedXmlError("Retention requires Mode and RetainUntilDate")
                : !TryParseMode(mode, out var rm)
                    ? new MalformedXmlError($"unknown Mode '{mode}'")
                    : !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                        ? new MalformedXmlError($"unparseable RetainUntilDate '{until}'")
                        : (Result<Retention>)new Retention(rm, dt);
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    public async Task<Result<bool>> ReadLegalHold(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, settings);
            string? status = null;
            string? current = null;
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                switch (r.NodeType)
                {
                    case XmlNodeType.Element: current = r.LocalName; break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        if (current is "Status") status = r.Value;
                        break;
                    case XmlNodeType.EndElement: current = null; break;
                }
            }
            return status switch
            {
                "ON" => true,
                "OFF" => false,
                _ => new MalformedXmlError($"LegalHold Status must be ON or OFF, got '{status}'"),
            };
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }
    }

    private static bool TryParseMode(string raw, out RetentionMode mode)
    {
        switch (raw)
        {
            case "GOVERNANCE": mode = RetentionMode.Governance; return true;
            case "COMPLIANCE": mode = RetentionMode.Compliance; return true;
            default: mode = default; return false;
        }
    }

    private async Task<BatchDeleteKey?> ReadObjectEntry(XmlReader r)
    {
        string? key = null;
        string? versionId = null;
        using var sub = r.ReadSubtree();
        var advanced = await sub.ReadAsync();
        while (advanced)
        {
            if (sub.NodeType is not XmlNodeType.Element) { advanced = await sub.ReadAsync(); continue; }
            if (sub.LocalName is "Key")
            {
                key = await sub.ReadElementContentAsStringAsync();
                advanced = !sub.EOF;
            }
            else if (sub.LocalName is "VersionId")
            {
                versionId = await sub.ReadElementContentAsStringAsync();
                advanced = !sub.EOF;
            }
            else
            {
                advanced = await sub.ReadAsync();
            }
        }
        return key is not null ? new BatchDeleteKey(key, versionId) : null;
    }
}
