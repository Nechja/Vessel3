namespace Vessel3.Storage;

internal static class YamlWebhookLoader
{
    public static Result<IReadOnlyList<Webhook>> LoadFromYaml(string yamlContent, TimeProvider? clock = null) =>
        WebhookYamlSerializer.Instance.LoadFromYaml(yamlContent, clock);

    public static string ExportToYaml(IEnumerable<Webhook> webhooks) =>
        WebhookYamlSerializer.Instance.ExportToYaml(webhooks);
}
