using System.Globalization;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed class ObjectXmlReader : IObjectXmlReader
{
    public async Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct)
    {
        List<BatchDeleteKey> keys = [];
        var quiet = false;

        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            var advanced = true;
            while (advanced)
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element)
                {
                    advanced = await r.ReadAsync();
                    continue;
                }

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
        List<CompletedPart> parts = [];

        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element || r.LocalName is not "Part") continue;

                var part = await ReadPartEntry(r);
                if (part is not null) parts.Add(part);
            }
        }
        catch (XmlException ex)
        {
            return new MalformedXmlError(ex.Message);
        }

        return parts;
    }

    public async Task<Result<IReadOnlyDictionary<string, string>>> ReadTagging(Stream input, CancellationToken ct)
    {
        List<KeyValuePair<string, string>> pairs = [];
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
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

    public async Task<Result<Retention>> ReadRetention(Stream input, CancellationToken ct)
    {
        try
        {
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? mode = null;
            string? until = null;
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
                        if (current is "Mode") mode = await r.GetValueAsync();
                        else if (current is "RetainUntilDate") until = await r.GetValueAsync();
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
                }
            }

            return mode is null || until is null
                ? new MalformedXmlError("Retention requires Mode and RetainUntilDate")
                : !S3XmlDefaults.TryParseMode(mode, out var rm)
                    ? new MalformedXmlError($"unknown Mode '{mode}'")
                    : !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                        ? new MalformedXmlError($"unparseable RetainUntilDate '{until}'")
                        : new Retention(rm, dt);
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
            using var r = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? status = null;
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
                        if (current is "Status") status = await r.GetValueAsync();
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
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
                    if (currentField is "Key") key = await sub.GetValueAsync();
                    else if (currentField is "Value") value = await sub.GetValueAsync();
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
                            if (int.TryParse(await sub.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) number = n;
                            break;
                        case "ETag": etag = (await sub.GetValueAsync()).Trim('"'); break;
                        case "ChecksumCRC32":   c32 = ChecksumAlgorithms.Base64ToHex(await sub.GetValueAsync()); break;
                        case "ChecksumCRC32C":  c32c = ChecksumAlgorithms.Base64ToHex(await sub.GetValueAsync()); break;
                        case "ChecksumSHA1":    s1 = ChecksumAlgorithms.Base64ToHex(await sub.GetValueAsync()); break;
                        case "ChecksumSHA256":  s256 = ChecksumAlgorithms.Base64ToHex(await sub.GetValueAsync()); break;
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

    private static async Task<BatchDeleteKey?> ReadObjectEntry(XmlReader r)
    {
        string? key = null;
        string? versionId = null;
        using var sub = r.ReadSubtree();
        var advanced = await sub.ReadAsync();
        while (advanced)
        {
            if (sub.NodeType is not XmlNodeType.Element)
            {
                advanced = await sub.ReadAsync();
                continue;
            }
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
