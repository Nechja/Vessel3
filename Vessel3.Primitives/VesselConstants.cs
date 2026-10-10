namespace Vessel3.Primitives;

public static class VesselHeaders
{
    public const string VersionId = "X-Vessel-Version-Id";
    public const string MetaPrefix = "X-Vessel-Meta-";
    public const string AccessKey = "X-Vessel-Key";
    public const string SecretKey = "X-Vessel-Secret";
    public const string AuthorizationSchemeBearer = "Bearer";
    public const string AuthorizationSchemeVessel = "Vessel";
    public const string BearerPrefix = "Bearer ";
    public const string VesselPrefix = "Vessel ";
}

public static class VesselMediaTypes
{
    public const string Json = "application/json";
    public const string OctetStream = "application/octet-stream";
    public const string CloudEventsJson = "application/cloudevents+json";
    public const string Utf8CharSet = "utf-8";
}

public static class VesselRoutes
{
    public const string V1Prefix = "/v1";
    public const string WhoAmI = "v1/iam/whoami";
    public const string Buckets = "v1/buckets";
    public const string Users = "v1/iam/users";
    public const string Keys = "v1/iam/keys";
    public const string Gc = "v1/admin/gc";
    public const string Sweep = "v1/admin/sweep";
    public const string Logs = "v1/admin/logs";
    public const string Webhooks = "v1/webhooks";
    public const string EventsStream = "v1/events/stream";
    public const string OciCatalog = "v2/_catalog";
}
