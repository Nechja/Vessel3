namespace Vessel3.Operator.Adapters.Kubernetes;

public static class KubernetesConstants
{
    public const string Group = "vessel.nechja.io";
    public const string Version = "v1alpha1";
    public const string ServerPlural = "vesselservers";
    public const string BucketPlural = "vesselbuckets";
    public const string UserPlural = "vesselusers";

    public const string AccessKeyField = "access-key";
    public const string SecretKeyField = "secret-key";
}
