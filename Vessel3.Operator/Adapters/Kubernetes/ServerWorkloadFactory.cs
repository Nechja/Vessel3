using k8s.Models;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Adapters.Kubernetes;

public static class ServerWorkloadFactory
{
    public static Dictionary<string, string> CreateLabels(string instanceName) => new()
    {
        [KubernetesConstants.LabelAppName] = KubernetesConstants.AppName,
        [KubernetesConstants.LabelAppInstance] = instanceName
    };

    public static V1Service CreateService(ServerDeclaration server)
    {
        var labels = CreateLabels(server.Identity.Name);
        List<V1ServicePort> ports =
        [
            new()
            {
                Name = KubernetesConstants.ServicePortName,
                Port = server.Port,
                TargetPort = server.Port
            }
        ];

        return new V1Service
        {
            Metadata = CreateMetadata(server.Identity.Name, server.Identity.Namespace, labels),
            Spec = new V1ServiceSpec
            {
                Type = KubernetesConstants.ServiceTypeClusterIp,
                Selector = labels,
                Ports = ports
            }
        };
    }

    public static V1StatefulSet CreateStatefulSet(ServerDeclaration server, ServerCredentials credentials)
    {
        var labels = CreateLabels(server.Identity.Name);
        var podTemplate = CreatePodTemplate(server, credentials, labels);
        var claimTemplates = CreateVolumeClaimTemplates(server);

        return new V1StatefulSet
        {
            Metadata = CreateMetadata(server.Identity.Name, server.Identity.Namespace, labels),
            Spec = new V1StatefulSetSpec
            {
                ServiceName = server.Identity.Name,
                Replicas = server.Replicas,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = podTemplate,
                VolumeClaimTemplates = claimTemplates
            }
        };
    }

    private static V1ObjectMeta CreateMetadata(string name, string @namespace, IDictionary<string, string> labels) =>
        new()
        {
            Name = name,
            NamespaceProperty = @namespace,
            Labels = labels
        };

    private static V1PodTemplateSpec CreatePodTemplate(
        ServerDeclaration server,
        ServerCredentials credentials,
        IDictionary<string, string> labels)
    {
        var container = CreateContainer(server, credentials);
        var securityContext = CreateSecurityContext();

        return new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = labels },
            Spec = new V1PodSpec
            {
                SecurityContext = securityContext,
                Containers = [container]
            }
        };
    }

    private static V1Container CreateContainer(ServerDeclaration server, ServerCredentials credentials)
    {
        var envVars = CreateEnvironmentVariables(server.Port, credentials);
        var volumeMounts = CreateVolumeMounts();
        var ports = CreateContainerPorts(server.Port);

        return new V1Container
        {
            Name = KubernetesConstants.AppName,
            Image = server.Image,
            ImagePullPolicy = server.ImagePullPolicy,
            Ports = ports,
            Env = envVars,
            VolumeMounts = volumeMounts
        };
    }

    private static List<V1EnvVar> CreateEnvironmentVariables(int port, ServerCredentials credentials) =>
    [
        new() { Name = KubernetesConstants.EnvAspNetCoreUrls, Value = $"http://0.0.0.0:{port}" },
        new() { Name = KubernetesConstants.EnvVesselData, Value = KubernetesConstants.DataMountPath },
        new() { Name = KubernetesConstants.EnvVesselAccessKey, Value = credentials.AccessKey },
        new() { Name = KubernetesConstants.EnvVesselSecretKey, Value = credentials.SecretKey },
        new() { Name = KubernetesConstants.EnvVesselRegion, Value = KubernetesConstants.DefaultRegion }
    ];

    private static List<V1VolumeMount> CreateVolumeMounts() =>
    [
        new()
        {
            Name = KubernetesConstants.DataVolumeName,
            MountPath = KubernetesConstants.DataMountPath
        }
    ];

    private static List<V1ContainerPort> CreateContainerPorts(int port) =>
    [
        new()
        {
            ContainerPort = port,
            Name = KubernetesConstants.ContainerPortName
        }
    ];

    private static V1PodSecurityContext CreateSecurityContext() =>
        new()
        {
            FsGroup = KubernetesConstants.DefaultSecurityContextUser,
            RunAsUser = KubernetesConstants.DefaultSecurityContextUser,
            RunAsNonRoot = true
        };

    private static List<V1PersistentVolumeClaim> CreateVolumeClaimTemplates(ServerDeclaration server) =>
    [
        new()
        {
            Metadata = new V1ObjectMeta { Name = KubernetesConstants.DataVolumeName },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = [KubernetesConstants.StorageAccessModeReadWriteOnce],
                StorageClassName = server.StorageClassName,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        [KubernetesConstants.StorageResourceName] = new(server.StorageSize)
                    }
                }
            }
        }
    ];
}
