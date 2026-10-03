namespace Vessel3.Operator.Domain.Models;

public readonly record struct ResourceIdentity(string Name, string Namespace)
{
    public string QualifiedName => $"{Namespace}/{Name}";

    public static ResourceIdentity Create(string name, string? @namespace = null) =>
        new(name, string.IsNullOrWhiteSpace(@namespace) ? "default" : @namespace);

    public override string ToString() => QualifiedName;
}
