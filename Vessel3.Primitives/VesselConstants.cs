namespace Vessel3.Primitives;

public static class VesselHeaders
{
    public const string VersionId = "X-Vessel-Version-Id";
    public const string MetaPrefix = "X-Vessel-Meta-";
    public const string AccessKey = "X-Vessel-Key";
    public const string SecretKey = "X-Vessel-Secret";
    public const string ClusterSecret = "X-Vessel-Cluster-Secret";
    public const string NodeId = "X-Vessel-Node-Id";
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
    public const string ClusterStatus = "v1/admin/cluster";
    public const string Webhooks = "v1/webhooks";
    public const string EventsStream = "v1/events/stream";
    public const string OciCatalog = "v2/_catalog";
    public const string GcRoute = $"/{Gc}";
    public const string SweepRoute = $"/{Sweep}";
    public const string LogsRoute = $"/{Logs}";
    public const string ClusterStatusRoute = $"/{ClusterStatus}";
    public const string ClusterGossipSubpath = "/gossip";
    public const string ClusterBlobsSubpath = "/blobs";
    public const string ClusterBlobRoutePattern = "/blobs/{sha}";
    public const string ClusterCatalogSubpath = "/catalog";
    public const string ClusterBucketEventsRoutePattern = "/buckets/{bucket}/events";
    public const string ClusterBucketSnapshotRoutePattern = "/buckets/{bucket}/snapshot";
    public const string ClusterBucketEventsSuffix = "/events";
    public const string ClusterBucketSnapshotSuffix = "/snapshot";
    public const string ClusterUploadsRoutePattern = "/uploads/{sessionId}";
    public const string ClusterIamDeltaSubpath = "/iam/delta";
    public const string ClusterGcReferencesSubpath = "/gc/references";
    public const string ClusterGossipPath = $"{ReservedRoutePrefixes.Cluster}{ClusterGossipSubpath}";
    public const string ClusterBlobsPrefix = $"{ReservedRoutePrefixes.Cluster}{ClusterBlobsSubpath}/";
    public const string ClusterCatalogPath = $"{ReservedRoutePrefixes.Cluster}{ClusterCatalogSubpath}";
    public const string ClusterBucketsPrefix = $"{ReservedRoutePrefixes.Cluster}/buckets/";
    public const string ClusterUploadsPrefix = $"{ReservedRoutePrefixes.Cluster}/uploads/";
    public const string ClusterIamDeltaPath = $"{ReservedRoutePrefixes.Cluster}{ClusterIamDeltaSubpath}";
    public const string ClusterGcReferencesPath = $"{ReservedRoutePrefixes.Cluster}{ClusterGcReferencesSubpath}";
}

public static class VesselQueryParams
{
    public const string MinBlobAgeSec = "minBlobAgeSec";
    public const string MinUploadAgeSec = "minUploadAgeSec";
    public const string Now = "now";
    public const string Limit = "limit";
    public const string Level = "level";
    public const string Protocol = "protocol";
    public const string AfterSeq = "afterSeq";
    public const string AfterGeneration = "afterGen";
    public const string OriginNodeId = "originNode";
}

public static class VesselProtocols
{
    public const string S3 = "s3";
    public const string Azure = "azure";
    public const string Oci = "oci";
    public const string Native = "native";
    public const string WebDav = "webdav";
    public const string Cluster = "cluster";
    public const string Http = "http";
}

public static class VesselDefaults
{
    public const string ServiceName = "vessel3";
    public const string DefaultVolumeId = "default";
    public const string DefaultVolumePool = "default";
    public const string DefaultRegion = "us-east-1";
    public const string DefaultLogFormat = "text";
    public const string AnonymousActor = "anonymous";
    public const string DataDirectoryName = "data";
    public const string BlobsSubdirectory = "blobs";
    public const string TmpSubdirectory = "tmp";
    public const string UploadsSubdirectory = "uploads";
    public const string GcTmpSubdirectory = "gc-tmp";
    public const string IamSubdirectory = "iam";
    public const string WebhooksSubdirectory = "webhooks";
    public const string WebhooksDefaultFileName = "webhooks.yaml";
    public const string XdgSubdirectory = ".xdg";
}

public static class VesselEnvVars
{
    public const string Data = "VESSEL3_DATA";
    public const string AccessKey = "VESSEL3_ACCESS_KEY";
    public const string SecretKey = "VESSEL3_SECRET_KEY";
    public const string Region = "VESSEL3_REGION";
    public const string Domain = "VESSEL3_DOMAIN";
    public const string GcMaxWaitSeconds = "VESSEL3_GC_MAX_WAIT_SECONDS";
    public const string LifecycleIntervalSeconds = "VESSEL3_LIFECYCLE_INTERVAL_SECONDS";
    public const string CompactIntervalSeconds = "VESSEL3_COMPACT_INTERVAL_SECONDS";
    public const string CompactThresholdBytes = "VESSEL3_COMPACT_THRESHOLD_BYTES";
    public const string SlowRequestMs = "VESSEL3_SLOW_REQUEST_MS";
    public const string MetricsToken = "VESSEL3_METRICS_TOKEN";
    public const string MetricsAllowAnonymous = "VESSEL3_METRICS_ALLOW_ANONYMOUS";
    public const string AdminUsers = "VESSEL3_ADMIN_USERS";
    public const string OciEnabled = "VESSEL3_OCI_ENABLED";
    public const string WebDavEnabled = "VESSEL3_WEBDAV_ENABLED";
    public const string WebhooksFile = "VESSEL3_WEBHOOKS_FILE";
    public const string LogFormat = "VESSEL3_LOG_FORMAT";
    public const string LogLevel = "VESSEL3_LOG_LEVEL";
    public const string AccessLog = "VESSEL3_ACCESS_LOG";
    public const string NodeId = "VESSEL3_NODE_ID";
    public const string AdvertiseUrl = "VESSEL3_ADVERTISE_URL";
    public const string Peers = "VESSEL3_PEERS";
    public const string ClusterSecret = "VESSEL3_CLUSTER_SECRET";
    public const string GossipIntervalMs = "VESSEL3_GOSSIP_INTERVAL_MS";
    public const string PeerSuspectMs = "VESSEL3_PEER_SUSPECT_MS";
    public const string PeerDeadMs = "VESSEL3_PEER_DEAD_MS";
    public const string OtelExporterOtlpEndpoint = "VESSEL3_OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string OtelEnabled = "VESSEL3_OTEL_ENABLED";
    public const string OtelServiceName = "VESSEL3_OTEL_SERVICE_NAME";
    public const string Volumes = "VESSEL3_VOLUMES";
    public const string XdgDataHome = "XDG_DATA_HOME";
}

public static class VesselVolumeStatuses
{
    public const string Online = "Online";
    public const string Standby = "Standby";
    public const string Waking = "Waking";
    public const string Degraded = "Degraded";
    public const string Faulted = "Faulted";
}

public static class VesselPeerStatuses
{
    public const string Alive = "Alive";
    public const string Suspect = "Suspect";
    public const string Dead = "Dead";
    public const string Left = "Left";
}

public static class VesselVolumeCapabilities
{
    public const string None = "None";
    public const string Ingest = "Ingest";
    public const string OnDemand = "OnDemand";
    public const string ReadOnly = "ReadOnly";
    public const string Mirrored = "Mirrored";
    public const string Remote = "Remote";
}

public static class VesselContextKeys
{
    public const string CallerIdentity = "CallerIdentity";
    public const string VirtualHostBucket = "VirtualHostBucket";
    public const string AzureRequestId = "AzureRequestId";
    public const string SigV4Context = "sigctx";
    public const string AnonymousAllowed = "AnonymousAllowed";
}

public static class CustomStatusCodes
{
    public const int ClientClosedRequest = 499;
}
