using System.Globalization;
using k8s.Models;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Adapters.Kubernetes;

public static class ServerWorkloadFactory
{
    public static bool IsClusterEnabled(ServerDeclaration server) =>
        server.Replicas > 1
        || server.Cluster?.Enabled == true
        || server.Cluster?.Peers is { Count: > 0 };

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
                PublishNotReadyAddresses = IsClusterEnabled(server) ? true : null,
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
        var envVars = CreateEnvironmentVariables(server, credentials);
        var volumeMounts = CreateVolumeMounts(server);
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

    private static List<V1EnvVar> CreateEnvironmentVariables(ServerDeclaration server, ServerCredentials credentials)
    {
        List<V1EnvVar> env =
        [
            new() { Name = KubernetesConstants.EnvAspNetCoreUrls, Value = $"http://0.0.0.0:{server.Port}" },
            new() { Name = KubernetesConstants.EnvVesselData, Value = KubernetesConstants.DataMountPath },
            new() { Name = KubernetesConstants.EnvVesselAccessKey, Value = credentials.AccessKey },
            new() { Name = KubernetesConstants.EnvVesselSecretKey, Value = credentials.SecretKey },
            new() { Name = KubernetesConstants.EnvVesselRegion, Value = KubernetesConstants.DefaultRegion }
        ];

        if (server.Volumes is { Count: > 0 } volumes)
        {
            var volumeSpec = string.Join(
                ",",
                volumes.Select(static v => $"{v.Id}:{v.Path}:{v.Pool}:{v.Capabilities}"));
            env.Add(new V1EnvVar { Name = KubernetesConstants.EnvVesselVolumes, Value = volumeSpec });
        }

        if (IsClusterEnabled(server))
        {
            env.Add(new V1EnvVar
            {
                Name = KubernetesConstants.EnvVesselNodeId,
                ValueFrom = new V1EnvVarSource
                {
                    FieldRef = new V1ObjectFieldSelector { FieldPath = KubernetesConstants.FieldPathMetadataName }
                }
            });
            env.Add(new V1EnvVar
            {
                Name = KubernetesConstants.EnvVesselAdvertiseUrl,
                Value = $"http://$({KubernetesConstants.EnvVesselNodeId}).{server.Identity.Name}.{server.Identity.Namespace}.{KubernetesConstants.ClusterDnsSuffix}:{server.Port}"
            });
            env.Add(new V1EnvVar
            {
                Name = KubernetesConstants.EnvVesselPeers,
                Value = BuildPeersList(server)
            });

            if (server.Cluster is { } cluster)
            {
                env.Add(new V1EnvVar
                {
                    Name = KubernetesConstants.EnvVesselGossipIntervalMs,
                    Value = cluster.GossipIntervalMs.ToString(CultureInfo.InvariantCulture)
                });
                env.Add(new V1EnvVar
                {
                    Name = KubernetesConstants.EnvVesselPeerSuspectMs,
                    Value = cluster.PeerSuspectMs.ToString(CultureInfo.InvariantCulture)
                });
                env.Add(new V1EnvVar
                {
                    Name = KubernetesConstants.EnvVesselPeerDeadMs,
                    Value = cluster.PeerDeadMs.ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        return env;
    }

    private static string BuildPeersList(ServerDeclaration server)
    {
        var peers = new List<string>(Math.Max(0, server.Replicas) + (server.Cluster?.Peers?.Count ?? 0));
        for (var i = 0; i < server.Replicas; i++)
        {
            peers.Add($"http://{server.Identity.Name}-{i}.{server.Identity.Name}.{server.Identity.Namespace}.{KubernetesConstants.ClusterDnsSuffix}:{server.Port}");
        }

        if (server.Cluster?.Peers is { Count: > 0 } extraPeers)
        {
            foreach (var peer in extraPeers)
            {
                if (!string.IsNullOrWhiteSpace(peer))
                    peers.Add(peer.TrimEnd('/'));
            }
        }

        return string.Join(',', peers);
    }

    private static List<V1VolumeMount> CreateVolumeMounts(ServerDeclaration server)
    {
        List<V1VolumeMount> mounts =
        [
            new()
            {
                Name = KubernetesConstants.DataVolumeName,
                MountPath = KubernetesConstants.DataMountPath
            }
        ];

        if (server.Volumes is { Count: > 0 } volumes)
        {
            foreach (var vol in volumes)
            {
                if (!string.IsNullOrWhiteSpace(vol.Size))
                {
                    mounts.Add(new V1VolumeMount
                    {
                        Name = $"{KubernetesConstants.VolumeClaimPrefix}{vol.Id}",
                        MountPath = vol.Path
                    });
                }
            }
        }

        return mounts;
    }

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

    private static List<V1PersistentVolumeClaim> CreateVolumeClaimTemplates(ServerDeclaration server)
    {
        List<V1PersistentVolumeClaim> claims =
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

        if (server.Volumes is { Count: > 0 } volumes)
        {
            foreach (var vol in volumes)
            {
                if (!string.IsNullOrWhiteSpace(vol.Size))
                {
                    claims.Add(new V1PersistentVolumeClaim
                    {
                        Metadata = new V1ObjectMeta { Name = $"{KubernetesConstants.VolumeClaimPrefix}{vol.Id}" },
                        Spec = new V1PersistentVolumeClaimSpec
                        {
                            AccessModes = [KubernetesConstants.StorageAccessModeReadWriteOnce],
                            StorageClassName = vol.StorageClassName ?? server.StorageClassName,
                            Resources = new V1VolumeResourceRequirements
                            {
                                Requests = new Dictionary<string, ResourceQuantity>
                                {
                                    [KubernetesConstants.StorageResourceName] = new(vol.Size)
                                }
                            }
                        }
                    });
                }
            }
        }

        return claims;
    }
}
