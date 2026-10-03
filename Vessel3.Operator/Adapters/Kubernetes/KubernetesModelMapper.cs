using Vessel3.Operator.Adapters.Kubernetes.Models;
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
        var bucketNs = string.IsNullOrWhiteSpace(cr.Metadata.Namespace) ? "default" : cr.Metadata.Namespace;
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
        var userNs = string.IsNullOrWhiteSpace(cr.Metadata.Namespace) ? "default" : cr.Metadata.Namespace;
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

    public static ServerStatusPatch ToPatch(ServerStatus status) =>
        new(new ServerStatusPatchBody(
            status.Phase,
            status.Endpoint,
            status.AdminSecret,
            status.ReadyReplicas,
            status.Conditions?.Select(ToAdapterCondition).ToList()));

    public static BucketStatusPatch ToPatch(BucketStatus status) =>
        new(new BucketStatusPatchBody(
            status.Phase,
            status.SizeBytes,
            status.ObjectCount,
            status.ErrorMessage,
            status.Conditions?.Select(ToAdapterCondition).ToList()));

    public static UserStatusPatch ToPatch(UserStatus status) =>
        new(new UserStatusPatchBody(
            status.Phase,
            status.UserId,
            status.SecretRef,
            status.ErrorMessage,
            status.Conditions?.Select(ToAdapterCondition).ToList()));

    private static Models.ResourceCondition ToAdapterCondition(Domain.Models.ResourceCondition c) =>
        new()
        {
            Type = c.Type,
            Status = c.Status,
            Reason = c.Reason,
            Message = c.Message
        };
}
