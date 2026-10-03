using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Adapters.Serialization;
using Vessel3.Operator.Domain.Models;

namespace Vessel3.Operator.Adapters.Kubernetes;

internal static class KubernetesModelMapper
{
    public static ServerDeclaration ToDeclaration(VesselServerCustomResource cr) =>
        new(
            Identity: ResourceIdentity.Create(cr.Metadata.Name, cr.Metadata.Namespace),
            Replicas: cr.Spec.Replicas,
            Port: cr.Spec.Service.Port,
            StorageSize: cr.Spec.Storage.Size,
            StorageClassName: cr.Spec.Storage.StorageClassName,
            AdminSecretName: cr.Spec.Auth.AdminSecretName,
            Image: cr.Spec.Image,
            ImagePullPolicy: cr.Spec.ImagePullPolicy,
            Domains: cr.Spec.Domains);

    public static BucketDeclaration ToDeclaration(VesselBucketCustomResource cr)
    {
        var bucketNs = string.IsNullOrWhiteSpace(cr.Metadata.Namespace) ? KubernetesConstants.DefaultNamespace : cr.Metadata.Namespace;
        var serverNs = string.IsNullOrWhiteSpace(cr.Spec.ServerRef.Namespace) ? bucketNs : cr.Spec.ServerRef.Namespace;

        BucketWebsiteDefinition? website = cr.Spec.Website is { } site
            ? new BucketWebsiteDefinition(site.IndexDocument, site.ErrorDocument)
            : null;

        return new(
            Identity: ResourceIdentity.Create(cr.Metadata.Name, bucketNs),
            ServerReference: ResourceIdentity.Create(cr.Spec.ServerRef.Name, serverNs),
            BucketName: cr.Spec.BucketName,
            Versioning: cr.Spec.Versioning,
            Access: cr.Spec.Access,
            PrunePolicy: cr.Spec.PrunePolicy,
            Website: website);
    }

    public static UserDeclaration ToDeclaration(VesselUserCustomResource cr)
    {
        var userNs = string.IsNullOrWhiteSpace(cr.Metadata.Namespace) ? KubernetesConstants.DefaultNamespace : cr.Metadata.Namespace;
        var serverNs = string.IsNullOrWhiteSpace(cr.Spec.ServerRef.Namespace) ? userNs : cr.Spec.ServerRef.Namespace;

        var secretOutput = new SecretOutputDefinition(
            Namespace: string.IsNullOrWhiteSpace(cr.Spec.WriteSecret.SecretNamespace) ? userNs : cr.Spec.WriteSecret.SecretNamespace,
            Name: cr.Spec.WriteSecret.SecretName,
            AccessKeyField: cr.Spec.WriteSecret.Keys.AccessKey,
            SecretKeyField: cr.Spec.WriteSecret.Keys.SecretKey,
            EndpointField: cr.Spec.WriteSecret.Keys.Endpoint,
            RegionField: cr.Spec.WriteSecret.Keys.Region);

        return new(
            Identity: ResourceIdentity.Create(cr.Metadata.Name, userNs),
            ServerReference: ResourceIdentity.Create(cr.Spec.ServerRef.Name, serverNs),
            Username: cr.Spec.Username,
            Role: cr.Spec.Role,
            SecretOutput: secretOutput);
    }

    public static ServerStatusPatch ToPatch(ServerResourceStatus status) =>
        new(new VesselServerStatus
        {
            Phase = status.Phase,
            Endpoint = status.Endpoint,
            AdminSecret = status.AdminSecret,
            ReadyReplicas = status.ReadyReplicas,
            Conditions = ToAdapterConditions(status.Conditions)
        });

    public static BucketStatusPatch ToPatch(BucketResourceStatus status) =>
        new(new VesselBucketStatus
        {
            Phase = status.Phase,
            SizeBytes = status.SizeBytes,
            ObjectCount = status.ObjectCount,
            Conditions = ToAdapterConditions(status.Conditions)
        });

    public static UserStatusPatch ToPatch(UserResourceStatus status) =>
        new(new VesselUserStatus
        {
            Phase = status.Phase,
            UserId = status.UserId,
            SecretRef = status.SecretRef,
            Conditions = ToAdapterConditions(status.Conditions)
        });

    private static List<Models.ResourceCondition>? ToAdapterConditions(IReadOnlyList<Domain.Models.ResourceCondition>? conditions) =>
        conditions is { Count: > 0 } ? [.. conditions.Select(ToAdapterCondition)] : null;

    private static Models.ResourceCondition ToAdapterCondition(Domain.Models.ResourceCondition c) =>
        new()
        {
            Type = c.Type,
            Status = c.Status,
            Reason = c.Reason,
            Message = c.Message
        };
}
