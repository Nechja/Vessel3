namespace Vessel3.Operator.Adapters.Kubernetes;

public static class KubernetesConstants
{
    public const string Group = "vessel.nechja.io";
    public const string Version = "v1alpha1";
    public const string ServerPlural = "vesselservers";
    public const string BucketPlural = "vesselbuckets";
    public const string UserPlural = "vesselusers";
    public const string WebhookPlural = "vesselwebhooks";

    public const string DefaultNamespace = "default";

    public const string AccessKeyField = "access-key";
    public const string SecretKeyField = "secret-key";

    public const string LabelAppName = "app.kubernetes.io/name";
    public const string LabelAppInstance = "app.kubernetes.io/instance";
    public const string AppName = "vessel3";

    public const string ServiceTypeClusterIp = "ClusterIP";
    public const string ServicePortName = "http-s3";
    public const string ContainerPortName = "http";

    public const string EnvAspNetCoreUrls = "ASPNETCORE_URLS";
    public const string EnvVesselData = "VESSEL3_DATA";
    public const string EnvVesselAccessKey = "VESSEL3_ACCESS_KEY";
    public const string EnvVesselSecretKey = "VESSEL3_SECRET_KEY";
    public const string EnvVesselRegion = "VESSEL3_REGION";

    public const string DataVolumeName = "data";
    public const string DataMountPath = "/data";
    public const string StorageAccessModeReadWriteOnce = "ReadWriteOnce";
    public const string StorageResourceName = "storage";
    public const string DefaultRegion = "us-east-1";

    public const string SecretTypeOpaque = "Opaque";
    public const int DefaultSecurityContextUser = 10001;
}
