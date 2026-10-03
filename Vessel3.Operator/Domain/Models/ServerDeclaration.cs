namespace Vessel3.Operator.Domain.Models;

public sealed record ServerDeclaration(
    ResourceIdentity Identity,
    int Replicas = 1,
    int Port = 9000,
    string StorageSize = "10Gi",
    string? StorageClassName = null,
    string? AdminSecretName = null,
    string Image = "ghcr.io/nechja/vessel3:latest",
    string ImagePullPolicy = "IfNotPresent",
    IReadOnlyList<string>? Domains = null);
