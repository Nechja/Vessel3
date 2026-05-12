using System.Globalization;
using System.Xml;
using Vessel3.Server;
using Vessel3.Server.Storage;

namespace Vessel3.Server.S3;

internal sealed record BatchDeleteKey(string Key, string? VersionId);
internal sealed record BatchDeleteRequest(IReadOnlyList<BatchDeleteKey> Keys, bool Quiet);
internal sealed record BatchDeleteOutcome(string Key, string? VersionId, Error? Error);
internal sealed record CompletedPart(int Number, string Etag);

internal interface IS3XmlReader
{
    Task<Result<BatchDeleteRequest>> ReadBatchDeleteRequest(Stream input, CancellationToken ct);
    Task<Result<IReadOnlyList<CompletedPart>>> ReadCompleteMultipartUploadRequest(Stream input, CancellationToken ct);
    Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct);
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
            while (await r.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType is not XmlNodeType.Element) continue;

                if (r.LocalName is "Object")
                {
                    var key = await ReadObjectEntry(r);
                    if (key is not null) keys.Add(key);
                }
                else if (r.LocalName is "Quiet")
                {
                    var raw = await r.ReadElementContentAsStringAsync();
                    quiet = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
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

    private static async Task<CompletedPart?> ReadPartEntry(XmlReader r)
    {
        int? number = null;
        string? etag = null;
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
                    if (currentField is "PartNumber"
                        && int.TryParse(sub.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        number = n;
                    else if (currentField is "ETag")
                        etag = sub.Value.Trim('"');
                    break;
                case XmlNodeType.EndElement:
                    currentField = null;
                    break;
            }
        }
        return number is { } n2 && etag is not null ? new CompletedPart(n2, etag) : null;
    }

    private async Task<BatchDeleteKey?> ReadObjectEntry(XmlReader r)
    {
        string? key = null;
        string? versionId = null;
        using var sub = r.ReadSubtree();
        while (await sub.ReadAsync())
        {
            if (sub.NodeType is not XmlNodeType.Element) continue;
            if (sub.LocalName is "Key") key = await sub.ReadElementContentAsStringAsync();
            else if (sub.LocalName is "VersionId") versionId = await sub.ReadElementContentAsStringAsync();
        }
        return key is not null ? new BatchDeleteKey(key, versionId) : null;
    }
}
