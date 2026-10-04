namespace Vessel3.Operator.Domain.Models;

public sealed record WebhookSecretRef(
    string Namespace,
    string Name,
    string Key = "secret");

public sealed record WebhookDeclaration(
    ResourceIdentity Identity,
    ResourceIdentity ServerReference,
    string Name,
    string Url,
    WebhookSecretRef? SecretRef = null,
    IReadOnlyList<string>? EventFilters = null,
    IReadOnlyList<string>? ResourceFilters = null,
    bool Active = true);
