using System.Globalization;

namespace Vessel3.Storage;

public static class VesselEventTypes
{
    public const string ObjectCreated = "object.created";
    public const string ObjectDeleted = "object.deleted";
    public const string BucketCreated = "bucket.created";
    public const string BucketDeleted = "bucket.deleted";
    public const string ContainerImagePushed = "container.image.pushed";
    public const string ContainerImageDeleted = "container.image.deleted";
    public const string UserCreated = "user.created";
    public const string UserDeleted = "user.deleted";
}

internal static class VesselEvents
{
    public static VesselEvent ObjectCreated(
        string bucket,
        string key,
        long size,
        string eTag,
        string versionId,
        string blobSha,
        string contentType,
        string protocol = "s3",
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.ObjectCreated,
            $"{bucket}/{key}",
            new Dictionary<string, string>(8)
            {
                ["bucket"] = bucket,
                ["key"] = key,
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
                ["eTag"] = eTag,
                ["versionId"] = versionId,
                ["blobSha"] = blobSha,
                ["contentType"] = contentType,
                ["protocol"] = protocol
            },
            actor,
            host);

    public static VesselEvent ObjectDeleted(
        string bucket,
        string key,
        string? versionId,
        bool isDeleteMarker,
        string protocol = "s3",
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.ObjectDeleted,
            $"{bucket}/{key}",
            new Dictionary<string, string>(5)
            {
                ["bucket"] = bucket,
                ["key"] = key,
                ["versionId"] = versionId ?? "null",
                ["deleteMarker"] = isDeleteMarker ? "true" : "false",
                ["protocol"] = protocol
            },
            actor,
            host);

    public static VesselEvent BucketCreated(
        string bucket,
        string? owner = null,
        string protocol = "s3",
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.BucketCreated,
            bucket,
            new Dictionary<string, string>(3)
            {
                ["bucket"] = bucket,
                ["owner"] = owner ?? "anonymous",
                ["protocol"] = protocol
            },
            actor,
            host);

    public static VesselEvent BucketDeleted(
        string bucket,
        string protocol = "s3",
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.BucketDeleted,
            bucket,
            new Dictionary<string, string>(2)
            {
                ["bucket"] = bucket,
                ["protocol"] = protocol
            },
            actor,
            host);

    public static VesselEvent ContainerImagePushed(
        string repository,
        string reference,
        string digest,
        string mediaType,
        long size,
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.ContainerImagePushed,
            $"{repository}:{reference}",
            new Dictionary<string, string>(5)
            {
                ["repository"] = repository,
                ["reference"] = reference,
                ["digest"] = digest,
                ["mediaType"] = mediaType,
                ["size"] = size.ToString(CultureInfo.InvariantCulture)
            },
            actor,
            host);

    public static VesselEvent ContainerImageDeleted(
        string repository,
        string reference,
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.ContainerImageDeleted,
            $"{repository}:{reference}",
            new Dictionary<string, string>(2)
            {
                ["repository"] = repository,
                ["reference"] = reference
            },
            actor,
            host);

    public static VesselEvent UserCreated(
        string userId,
        string role,
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.UserCreated,
            userId,
            new Dictionary<string, string>(2)
            {
                ["userId"] = userId,
                ["role"] = role
            },
            actor ?? "admin",
            host);

    public static VesselEvent UserDeleted(
        string userId,
        string? actor = null,
        string? host = null) =>
        CreateEvent(
            VesselEventTypes.UserDeleted,
            userId,
            new Dictionary<string, string>(1)
            {
                ["userId"] = userId
            },
            actor ?? "admin",
            host);

    private static VesselEvent CreateEvent(
        string type,
        string subject,
        IReadOnlyDictionary<string, string> data,
        string? actor,
        string? host) =>
        new(
            "evt_" + Ulid.NewUlid().ToString(),
            type,
            "/vessel3",
            subject,
            DateTimeOffset.UtcNow,
            data,
            Actor: actor ?? "anonymous",
            Host: host);
}
