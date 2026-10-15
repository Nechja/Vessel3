using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Vessel3.Storage;

internal sealed class WebhookYamlSerializer : IWebhookYamlSerializer
{
    public static WebhookYamlSerializer Instance { get; } = new();

    public Result<IReadOnlyList<Webhook>> LoadFromYaml(string yamlContent, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        List<Webhook> list = [];
        if (string.IsNullOrWhiteSpace(yamlContent))
            return list;

        var lines = yamlContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var inWebhooks = false;
        var builder = new WebhookEntryBuilder();

        void FlushCurrent()
        {
            var webhook = builder.Build(clock);
            if (webhook is not null)
            {
                list.Add(webhook);
            }
            builder.Reset();
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

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("      ", StringComparison.Ordinal) && !line.StartsWith("\t\t", StringComparison.Ordinal))
            {
                FlushCurrent();
                var remainder = trimmed[2..].Trim();
                if (!string.IsNullOrEmpty(remainder))
                {
                    builder.ParseKeyValue(remainder);
                }
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && builder.ListProperty is not null)
            {
                var itemVal = StripQuotes(trimmed[2..].Trim());
                if (builder.ListProperty == "events") builder.Events.Add(itemVal);
                else if (builder.ListProperty == "resources") builder.Resources.Add(itemVal);
                continue;
            }

            builder.ParseKeyValue(trimmed);
        }

        FlushCurrent();
        return list;
    }

    private sealed class WebhookEntryBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Secret { get; set; }
        public List<string> Events { get; } = [];
        public List<string> Resources { get; } = [];
        public bool IsActive { get; set; } = true;
        public string? ListProperty { get; set; }

        public void Reset()
        {
            Id = null;
            Name = null;
            Url = null;
            Secret = null;
            Events.Clear();
            Resources.Clear();
            IsActive = true;
            ListProperty = null;
        }

        public Webhook? Build(TimeProvider clock)
        {
            if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Name))
            {
                return null;
            }

            var finalId = !string.IsNullOrWhiteSpace(Id)
                ? Id
                : "whk_yaml_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Name}:{Url}")))[..12];

            var eventFilters = Events.Count > 0 ? Events.ToList() : ["*"];
            var resourceFilters = Resources.Count > 0 ? Resources.ToList() : null;

            return new Webhook(
                finalId,
                Name.Trim(),
                Url.Trim(),
                Secret,
                eventFilters,
                resourceFilters,
                IsActive,
                clock.GetUtcNow(),
                null,
                null,
                null,
                IsStatic: true);
        }

        private static readonly FrozenDictionary<string, Action<WebhookEntryBuilder, string>> PropertyParsers =
            new Dictionary<string, Action<WebhookEntryBuilder, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = (builder, value) => { builder.Id = StripQuotes(value); builder.ListProperty = null; },
                ["name"] = (builder, value) => { builder.Name = StripQuotes(value); builder.ListProperty = null; },
                ["url"] = (builder, value) => { builder.Url = StripQuotes(value); builder.ListProperty = null; },
                ["secret"] = (builder, value) => { builder.Secret = string.IsNullOrEmpty(value) ? null : StripQuotes(value); builder.ListProperty = null; },
                ["active"] = (builder, value) => { builder.IsActive = IsTruthy(value); builder.ListProperty = null; },
                ["enabled"] = (builder, value) => { builder.IsActive = IsTruthy(value); builder.ListProperty = null; },
                ["events"] = (builder, _) => builder.ListProperty = "events",
                ["resources"] = (builder, _) => builder.ListProperty = "resources",
                ["repositories"] = (builder, _) => builder.ListProperty = "resources",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        private static bool IsTruthy(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1";

        public void ParseKeyValue(string line)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) return;

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            if (PropertyParsers.TryGetValue(key, out var parser))
            {
                parser(this, value);
                return;
            }

            ListProperty = null;
        }
    }

    private static string StripQuotes(string input) =>
        input.Length >= 2 && ((input.StartsWith('"') && input.EndsWith('"')) || (input.StartsWith('\'') && input.EndsWith('\'')))
            ? input[1..^1]
            : input;

    public string ExportToYaml(IEnumerable<Webhook> webhooks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vessel3 Webhook Declarations");
        builder.AppendLine("webhooks:");

        foreach (var webhook in webhooks)
        {
            builder.Append("  - id: ").AppendLine(EscapeYaml(webhook.Id));
            builder.Append("    name: \"").Append(EscapeYaml(webhook.Name)).AppendLine("\"");
            builder.Append("    url: \"").Append(EscapeYaml(webhook.Url)).AppendLine("\"");
            if (!string.IsNullOrEmpty(webhook.Secret))
            {
                builder.Append("    secret: \"").Append(EscapeYaml(webhook.Secret)).AppendLine("\"");
            }
            builder.Append("    active: ").AppendLine(webhook.Active ? "true" : "false");

            if (webhook.EventFilters is { Count: > 0 } eventFilters)
            {
                builder.AppendLine("    events:");
                foreach (var eventFilter in eventFilters)
                {
                    builder.Append("      - ").AppendLine(EscapeYaml(eventFilter));
                }
            }

            if (webhook.ResourceFilters is { Count: > 0 } resourceFilters)
            {
                builder.AppendLine("    resources:");
                foreach (var resourceFilter in resourceFilters)
                {
                    builder.Append("      - ").AppendLine(EscapeYaml(resourceFilter));
                }
            }
        }

        return builder.ToString();
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
