namespace Vessel3.Operator.Domain.Models;

public sealed record BucketWebsiteDefinition(
    string IndexDocument = "index.html",
    string? ErrorDocument = null);
