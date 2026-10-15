namespace Vessel3.Storage;

internal interface IWebhookYamlSerializer
{
    Result<IReadOnlyList<Webhook>> LoadFromYaml(string yamlContent, TimeProvider? clock = null);
    string ExportToYaml(IEnumerable<Webhook> webhooks);
}
