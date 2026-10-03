using System.Security.Cryptography;
using System.Text;
using Vessel3.Primitives;

namespace Vessel3.Storage;

internal static class YamlWebhookLoader
{
    public static Result<IReadOnlyList<Webhook>> LoadFromYaml(string yamlContent, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var list = new List<Webhook>();
        if (string.IsNullOrWhiteSpace(yamlContent))
            return list;

        var lines = yamlContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var inWebhooks = false;

        string? curId = null;
        string? curName = null;
        string? curUrl = null;
        string? curSecret = null;
        var curEvents = new List<string>();
        var curResources = new List<string>();
        var curActive = true;
        string? curListProperty = null;

        void FlushCurrent()
        {
            if (string.IsNullOrWhiteSpace(curUrl) || string.IsNullOrWhiteSpace(curName))
            {
                curId = null;
                curName = null;
                curUrl = null;
                curSecret = null;
                curEvents.Clear();
                curResources.Clear();
                curActive = true;
                curListProperty = null;
                return;
            }

            var finalId = !string.IsNullOrWhiteSpace(curId)
                ? curId
                : "whk_yaml_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{curName}:{curUrl}")))[..12];

            var events = curEvents.Count > 0 ? curEvents.ToList() : ["*"];
            var resources = curResources.Count > 0 ? curResources.ToList() : null;

            list.Add(new Webhook(
                finalId,
                curName.Trim(),
                curUrl.Trim(),
                curSecret,
                events,
                resources,
                curActive,
                clock.GetUtcNow(),
                null,
                null,
                null,
                IsStatic: true));

            curId = null;
            curName = null;
            curUrl = null;
            curSecret = null;
            curEvents.Clear();
            curResources.Clear();
            curActive = true;
            curListProperty = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (trimmed == "webhooks:" || trimmed.StartsWith("webhooks:", StringComparison.Ordinal))
            {
                inWebhooks = true;
                continue;
            }

            if (!inWebhooks) continue;

            // Check if starting a new webhook item: "- "
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("      ", StringComparison.Ordinal) && !line.StartsWith("\t\t", StringComparison.Ordinal))
            {
                FlushCurrent();
                var remainder = trimmed[2..].Trim();
                if (!string.IsNullOrEmpty(remainder))
                {
                    ParseKeyValue(remainder, ref curId, ref curName, ref curUrl, ref curSecret, ref curActive, ref curListProperty);
                }
                continue;
            }

            // Check if item in a list property (events or resources)
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && curListProperty is not null)
            {
                var itemVal = StripQuotes(trimmed[2..].Trim());
                if (curListProperty == "events") curEvents.Add(itemVal);
                else if (curListProperty == "resources") curResources.Add(itemVal);
                continue;
            }

            // Normal key-value property
            ParseKeyValue(trimmed, ref curId, ref curName, ref curUrl, ref curSecret, ref curActive, ref curListProperty);
        }

        FlushCurrent();
        return list;
    }

    private static void ParseKeyValue(
        string line,
        ref string? curId,
        ref string? curName,
        ref string? curUrl,
        ref string? curSecret,
        ref bool curActive,
        ref string? curListProperty)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx <= 0) return;

        var key = line[..colonIdx].Trim().ToLowerInvariant();
        var val = line[(colonIdx + 1)..].Trim();

        switch (key)
        {
            case "id":
                curId = StripQuotes(val);
                curListProperty = null;
                break;
            case "name":
                curName = StripQuotes(val);
                curListProperty = null;
                break;
            case "url":
                curUrl = StripQuotes(val);
                curListProperty = null;
                break;
            case "secret":
                curSecret = string.IsNullOrEmpty(val) ? null : StripQuotes(val);
                curListProperty = null;
                break;
            case "active":
            case "enabled":
                curActive = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("yes", StringComparison.OrdinalIgnoreCase) || val == "1";
                curListProperty = null;
                break;
            case "events":
                curListProperty = "events";
                break;
            case "resources":
            case "repositories":
                curListProperty = "resources";
                break;
            default:
                curListProperty = null;
                break;
        }
    }

    private static string StripQuotes(string str) =>
        str.Length >= 2 && ((str.StartsWith('"') && str.EndsWith('"')) || (str.StartsWith('\'') && str.EndsWith('\'')))
            ? str[1..^1]
            : str;

    public static string ExportToYaml(IEnumerable<Webhook> webhooks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Vessel3 Webhook Declarations");
        sb.AppendLine("webhooks:");

        foreach (var w in webhooks)
        {
            sb.Append("  - id: ").AppendLine(EscapeYaml(w.Id));
            sb.Append("    name: \"").Append(EscapeYaml(w.Name)).AppendLine("\"");
            sb.Append("    url: \"").Append(EscapeYaml(w.Url)).AppendLine("\"");
            if (!string.IsNullOrEmpty(w.Secret))
            {
                sb.Append("    secret: \"").Append(EscapeYaml(w.Secret)).AppendLine("\"");
            }
            sb.Append("    active: ").AppendLine(w.Active ? "true" : "false");

            if (w.EventFilters is { Count: > 0 } evts)
            {
                sb.AppendLine("    events:");
                foreach (var e in evts)
                {
                    sb.Append("      - ").AppendLine(EscapeYaml(e));
                }
            }

            if (w.ResourceFilters is { Count: > 0 } res)
            {
                sb.AppendLine("    resources:");
                foreach (var r in res)
                {
                    sb.Append("      - ").AppendLine(EscapeYaml(r));
                }
            }
        }

        return sb.ToString();
    }

    private static string EscapeYaml(string val) =>
        val.Replace("\"", "\\\"", StringComparison.Ordinal);
}
