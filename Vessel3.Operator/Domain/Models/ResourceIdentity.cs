namespace Vessel3.Operator.Domain.Models;

public readonly record struct ResourceIdentity(string Name, string Namespace)
{
    public const string DefaultNamespace = "default";

    public string QualifiedName => $"{Namespace}/{Name}";

    public string BuildClusterEndpoint(int port = 9000) => $"http://{Name}.{Namespace}.svc:{port}";

    public static ResourceIdentity Create(string name, string? @namespace = null) =>
        new(name, string.IsNullOrWhiteSpace(@namespace) ? DefaultNamespace : @namespace);

    public override string ToString() => QualifiedName;
}
