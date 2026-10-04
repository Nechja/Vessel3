using System.Globalization;
using System.Xml;

namespace Vessel3.Server.S3;

internal sealed class ObjectXmlReader : IObjectXmlReader
{
    public async Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct)
    {
        List<BatchDeleteKey> keys = [];
        var quiet = false;

        try
        {
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            var advanced = true;
            while (advanced)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is not XmlNodeType.Element)
                {
                    advanced = await reader.ReadAsync();
                    continue;
                }

                if (reader.LocalName is "Object")
                {
                    var key = await ReadObjectEntry(reader);
                    if (key is not null) keys.Add(key);
                    advanced = await reader.ReadAsync();
                }
                else if (reader.LocalName is "Quiet")
                {
                    var raw = await reader.ReadElementContentAsStringAsync();
                    quiet = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                    advanced = !reader.EOF;
                }
                else
                {
                    advanced = await reader.ReadAsync();
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is not XmlNodeType.Element || reader.LocalName is not "Part") continue;

                var part = await ReadPartEntry(reader);
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is not XmlNodeType.Element || reader.LocalName is not "Tag") continue;
                var (tagKey, tagValue) = await ReadTagEntry(reader);
                if (tagKey is null) continue;
                pairs.Add(new KeyValuePair<string, string>(tagKey, tagValue ?? string.Empty));
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? mode = null;
            string? until = null;
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
                        if (current is "Mode") mode = await reader.GetValueAsync();
                        else if (current is "RetainUntilDate") until = await reader.GetValueAsync();
                        break;
                    case XmlNodeType.EndElement:
                        current = null;
                        break;
                }
            }

            return mode is null || until is null
                ? new MalformedXmlError("Retention requires Mode and RetainUntilDate")
                : !S3XmlDefaults.TryParseMode(mode, out var retentionMode)
                    ? new MalformedXmlError($"unknown Mode '{mode}'")
                    : !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate)
                        ? new MalformedXmlError($"unparseable RetainUntilDate '{until}'")
                        : new Retention(retentionMode, parsedDate);
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
            using var reader = XmlReader.Create(input, S3XmlDefaults.ReaderSettings);
            string? status = null;
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
                        if (current is "Status") status = await reader.GetValueAsync();
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

    private static async Task<(string? Key, string? Value)> ReadTagEntry(XmlReader reader)
    {
        string? key = null;
        string? value = null;
        string? currentField = null;
        using var subtree = reader.ReadSubtree();
        while (await subtree.ReadAsync())
        {
            switch (subtree.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = subtree.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    if (currentField is "Key") key = await subtree.GetValueAsync();
                    else if (currentField is "Value") value = await subtree.GetValueAsync();
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }
        return (key, value);
    }

    private static async Task<CompletedPart?> ReadPartEntry(XmlReader reader)
    {
        int? number = null;
        string? etag = null;
        string? crc32 = null, crc32C = null, sha1 = null, sha256 = null;
        string? currentField = null;
        using var subtree = reader.ReadSubtree();
        while (await subtree.ReadAsync())
        {
            switch (subtree.NodeType)
            {
                case XmlNodeType.Element:
                    currentField = subtree.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    switch (currentField)
                    {
                        case "PartNumber":
                            if (int.TryParse(await subtree.GetValueAsync(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber)) number = parsedNumber;
                            break;
                        case "ETag": etag = (await subtree.GetValueAsync()).Trim('"'); break;
                        case "ChecksumCRC32": crc32 = ChecksumAlgorithms.Base64ToHex(await subtree.GetValueAsync()); break;
                        case "ChecksumCRC32C": crc32C = ChecksumAlgorithms.Base64ToHex(await subtree.GetValueAsync()); break;
                        case "ChecksumSHA1": sha1 = ChecksumAlgorithms.Base64ToHex(await subtree.GetValueAsync()); break;
                        case "ChecksumSHA256": sha256 = ChecksumAlgorithms.Base64ToHex(await subtree.GetValueAsync()); break;
                    }
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }
        if (number is not { } partNumber || etag is null) return null;
        CompletedPartChecksums? checksums = (crc32 ?? crc32C ?? sha1 ?? sha256) is null ? null : new CompletedPartChecksums(crc32, crc32C, sha1, sha256);
        return new CompletedPart(partNumber, etag, checksums);
    }

    private static async Task<BatchDeleteKey?> ReadObjectEntry(XmlReader reader)
    {
        string? key = null;
        string? versionId = null;
        using var subtree = reader.ReadSubtree();
        var advanced = await subtree.ReadAsync();
        while (advanced)
        {
            if (subtree.NodeType is not XmlNodeType.Element)
            {
                advanced = await subtree.ReadAsync();
                continue;
            }
            if (subtree.LocalName is "Key")
            {
                key = await subtree.ReadElementContentAsStringAsync();
                advanced = !subtree.EOF;
            }
            else if (subtree.LocalName is "VersionId")
            {
                versionId = await subtree.ReadElementContentAsStringAsync();
                advanced = !subtree.EOF;
            }
            else
            {
                advanced = await subtree.ReadAsync();
            }
        }
        return key is not null ? new BatchDeleteKey(key, versionId) : null;
    }
}
