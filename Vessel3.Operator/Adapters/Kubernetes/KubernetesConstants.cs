using Vessel3.Operator.Domain.Models;
using Vessel3.Primitives;

namespace Vessel3.Operator.Adapters.Kubernetes;

public static class KubernetesConstants
{
    public const string Group = "vessel.nechja.io";
    public const string Version = "v1alpha1";
    public const string ApiVersion = $"{Group}/{Version}";
    public const string ServerKind = "VesselServer";
    public const string BucketKind = "VesselBucket";
    public const string UserKind = "VesselUser";
    public const string WebhookKind = "VesselWebhook";
    public const string ServerPlural = "vesselservers";
    public const string BucketPlural = "vesselbuckets";
    public const string UserPlural = "vesselusers";
    public const string WebhookPlural = "vesselwebhooks";

    public const string DefaultNamespace = ResourceIdentity.DefaultNamespace;

    public const string AccessKeyField = "access-key";
    public const string SecretKeyField = "secret-key";

    public const string LabelAppName = "app.kubernetes.io/name";
    public const string LabelAppInstance = "app.kubernetes.io/instance";
    public const string AppName = VesselDefaults.ServiceName;

    public const string ServiceTypeClusterIp = "ClusterIP";
    public const string ClusterIpNone = "None";
    public const string PeersServiceSuffix = "-peers";
    public const string ServicePortName = "http-s3";
    public const string ContainerPortName = "http";
    public const int DefaultServerPort = ServerDefaults.Port;
    public const string DefaultServerImage = ServerDefaults.Image;
    public const string DefaultImagePullPolicy = ServerDefaults.ImagePullPolicy;
    public const string DefaultStorageSize = ServerDefaults.StorageSize;
    public const string DefaultVolumePool = ServerDefaults.VolumePool;
    public const string DefaultVolumeCapabilities = ServerDefaults.VolumeCapabilities;
    public const long DefaultGossipIntervalMs = ServerDefaults.GossipIntervalMs;
    public const long DefaultPeerSuspectMs = ServerDefaults.PeerSuspectMs;
    public const long DefaultPeerDeadMs = ServerDefaults.PeerDeadMs;
    public const string VolumeClaimPrefix = "vol-";
    public const string ClusterDnsSuffix = "svc.cluster.local";
    public const string AdminCredsSuffix = ServerDefaults.AdminCredsSuffix;

    public const string EnvAspNetCoreUrls = "ASPNETCORE_URLS";
    public const string EnvVesselData = VesselEnvVars.Data;
    public const string EnvVesselAccessKey = VesselEnvVars.AccessKey;
    public const string EnvVesselSecretKey = VesselEnvVars.SecretKey;
    public const string EnvVesselRegion = VesselEnvVars.Region;
    public const string EnvVesselNodeId = VesselEnvVars.NodeId;
    public const string EnvVesselAdvertiseUrl = VesselEnvVars.AdvertiseUrl;
    public const string EnvVesselPeers = VesselEnvVars.Peers;
    public const string EnvVesselClusterSecret = VesselEnvVars.ClusterSecret;
    public const string EnvVesselGossipIntervalMs = VesselEnvVars.GossipIntervalMs;
    public const string EnvVesselPeerSuspectMs = VesselEnvVars.PeerSuspectMs;
    public const string EnvVesselPeerDeadMs = VesselEnvVars.PeerDeadMs;
    public const string EnvVesselVolumes = VesselEnvVars.Volumes;
    public const string FieldPathMetadataName = "metadata.name";

    public const string DataVolumeName = VesselDefaults.DataDirectoryName;
    public const string DataMountPath = "/data";
    public const string StorageAccessModeReadWriteOnce = "ReadWriteOnce";
    public const string StorageResourceName = "storage";
    public const string DefaultRegion = VesselDefaults.DefaultRegion;

    public const string SecretTypeOpaque = "Opaque";
    public const int DefaultSecurityContextUser = 10001;
    public const string ErrorCodeNotFound = "NotFound";
    public const string ErrorCodeUnexpected = "Unexpected";
    public const string AccessKeyPrefix = "V3AK";
    public const string JsonPropertyItems = "items";
}
