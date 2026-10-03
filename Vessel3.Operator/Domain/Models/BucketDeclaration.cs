namespace Vessel3.Operator.Domain.Models;

public sealed record BucketDeclaration(
    ResourceIdentity Identity,
    ResourceIdentity ServerReference,
    string BucketName,
    string Versioning = "Disabled",
    string Access = "private",
    string PrunePolicy = "Retain",
    BucketWebsiteDefinition? Website = null);
