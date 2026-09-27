namespace Vessel3.Client;

public sealed record VesselClientOptions(
    string? BaseUrl = null,
    string? AccessKey = null,
    string? SecretKey = null,
    string? BearerToken = null);
