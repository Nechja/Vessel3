namespace Vessel3.Operator.Domain.Models;

public sealed record SecretOutputDefinition(
    string Namespace,
    string Name,
    string AccessKeyField = "AWS_ACCESS_KEY_ID",
    string SecretKeyField = "AWS_SECRET_ACCESS_KEY",
    string EndpointField = "AWS_ENDPOINT_URL_S3",
    string RegionField = "AWS_DEFAULT_REGION");
