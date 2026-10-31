using Vessel3.Operator.Adapters.Kubernetes;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Xunit;

namespace Vessel3.Tests.Operator;

public sealed class ServerWorkloadFactoryTests
{
    private static readonly ServerDeclaration TestServer = new(
        Identity: new ResourceIdentity("prod-vessel", "storage"),
        Replicas: 3,
        Port: 9000,
        StorageSize: "50Gi",
        StorageClassName: "fast-ebs",
        Image: "custom-vessel:v3.2",
        ImagePullPolicy: "Always");

    private static readonly ServerCredentials TestCredentials = new("TESTAK", "TESTSK");

    [Fact]
    public void CreateService_BuildsExpectedLabelsAndPorts()
    {
        var service = ServerWorkloadFactory.CreateService(TestServer);

        Assert.Equal("prod-vessel", service.Metadata.Name);
        Assert.Equal("storage", service.Metadata.NamespaceProperty);
        Assert.Equal("vessel3", service.Metadata.Labels[KubernetesConstants.LabelAppName]);
        Assert.Equal("prod-vessel", service.Metadata.Labels[KubernetesConstants.LabelAppInstance]);
        Assert.Equal(KubernetesConstants.ServiceTypeClusterIp, service.Spec.Type);
        Assert.Single(service.Spec.Ports);

        var port = service.Spec.Ports[0];
        Assert.Equal(KubernetesConstants.ServicePortName, port.Name);
        Assert.Equal(9000, port.Port);
        Assert.Equal("9000", port.TargetPort.Value);

        var peersService = ServerWorkloadFactory.CreatePeersService(TestServer);
        Assert.Equal("prod-vessel-peers", peersService.Metadata.Name);
        Assert.Equal(KubernetesConstants.ClusterIpNone, peersService.Spec.ClusterIP);
        Assert.True(peersService.Spec.PublishNotReadyAddresses);
    }

    [Fact]
    public void CreateStatefulSet_BuildsExpectedSecurityContextAndEnvironment()
    {
        var statefulSet = ServerWorkloadFactory.CreateStatefulSet(TestServer, TestCredentials);

        Assert.Equal("prod-vessel", statefulSet.Metadata.Name);
        Assert.Equal("storage", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal(3, statefulSet.Spec.Replicas);
        Assert.Equal("prod-vessel-peers", statefulSet.Spec.ServiceName);

        var podSpec = statefulSet.Spec.Template.Spec;
        Assert.Equal(KubernetesConstants.DefaultSecurityContextUser, podSpec.SecurityContext.FsGroup);
        Assert.Equal(KubernetesConstants.DefaultSecurityContextUser, podSpec.SecurityContext.RunAsUser);
        Assert.True(podSpec.SecurityContext.RunAsNonRoot);

        Assert.Single(podSpec.Containers);
        var container = podSpec.Containers[0];
        Assert.Equal(KubernetesConstants.AppName, container.Name);
        Assert.Equal("custom-vessel:v3.2", container.Image);
        Assert.Equal("Always", container.ImagePullPolicy);

        var env = container.Env.ToDictionary(e => e.Name, e => e.Value);
        Assert.Equal("http://0.0.0.0:9000", env[KubernetesConstants.EnvAspNetCoreUrls]);
        Assert.Equal(KubernetesConstants.DataMountPath, env[KubernetesConstants.EnvVesselData]);
        Assert.Equal("TESTAK", env[KubernetesConstants.EnvVesselAccessKey]);
        Assert.Equal("TESTSK", env[KubernetesConstants.EnvVesselSecretKey]);
        Assert.Equal(KubernetesConstants.DefaultRegion, env[KubernetesConstants.EnvVesselRegion]);
        Assert.Equal("http://$(VESSEL3_NODE_ID).prod-vessel-peers.storage.svc.cluster.local:9000", env[KubernetesConstants.EnvVesselAdvertiseUrl]);
        Assert.Equal(
            "http://prod-vessel-0.prod-vessel-peers.storage.svc.cluster.local:9000,http://prod-vessel-1.prod-vessel-peers.storage.svc.cluster.local:9000,http://prod-vessel-2.prod-vessel-peers.storage.svc.cluster.local:9000",
            env[KubernetesConstants.EnvVesselPeers]);

        var nodeIdEnv = Assert.Single(container.Env, e => e.Name == KubernetesConstants.EnvVesselNodeId);
        Assert.Equal(KubernetesConstants.FieldPathMetadataName, nodeIdEnv.ValueFrom.FieldRef.FieldPath);
    }

    [Fact]
    public void CreateStatefulSet_WithMultiVolumesAndCluster_EmitsVolumesEnvMountsAndPvcs()
    {
        var server = TestServer with
        {
            Volumes =
            [
                new ServerVolumeDeclaration("hot-nvme", "/volumes/hot", "nvme", "Ingest", "100Gi", "fast-nvme"),
                new ServerVolumeDeclaration("cold-archive", "/data/cold", "archive", "OnDemand"),
            ],
            Cluster = new ServerClusterDeclaration(Enabled: true, GossipIntervalMs: 500, PeerSuspectMs: 2500, PeerDeadMs: 7500)
        };

        var statefulSet = ServerWorkloadFactory.CreateStatefulSet(server, TestCredentials);
        var container = Assert.Single(statefulSet.Spec.Template.Spec.Containers);
        var env = container.Env.ToDictionary(e => e.Name, e => e.Value);

        Assert.Equal("hot-nvme:/volumes/hot:nvme:Ingest,cold-archive:/data/cold:archive:OnDemand", env[KubernetesConstants.EnvVesselVolumes]);
        Assert.Equal("500", env[KubernetesConstants.EnvVesselGossipIntervalMs]);
        Assert.Equal("2500", env[KubernetesConstants.EnvVesselPeerSuspectMs]);
        Assert.Equal("7500", env[KubernetesConstants.EnvVesselPeerDeadMs]);

        Assert.Equal(2, container.VolumeMounts.Count);
        Assert.Contains(container.VolumeMounts, m => m.Name == "vol-hot-nvme" && m.MountPath == "/volumes/hot");

        Assert.Equal(2, statefulSet.Spec.VolumeClaimTemplates.Count);
        Assert.Contains(statefulSet.Spec.VolumeClaimTemplates, pvc =>
            pvc.Metadata.Name == "vol-hot-nvme"
            && pvc.Spec.StorageClassName == "fast-nvme"
            && pvc.Spec.Resources.Requests[KubernetesConstants.StorageResourceName].ToString() == "100Gi");
    }

    [Fact]
    public void CreateStatefulSet_BuildsExpectedVolumeClaimTemplates()
    {
        var statefulSet = ServerWorkloadFactory.CreateStatefulSet(TestServer, TestCredentials);

        Assert.Single(statefulSet.Spec.VolumeClaimTemplates);
        var pvc = statefulSet.Spec.VolumeClaimTemplates[0];

        Assert.Equal(KubernetesConstants.DataVolumeName, pvc.Metadata.Name);
        Assert.Equal("fast-ebs", pvc.Spec.StorageClassName);
        Assert.Contains(KubernetesConstants.StorageAccessModeReadWriteOnce, pvc.Spec.AccessModes);
        Assert.Equal("50Gi", pvc.Spec.Resources.Requests[KubernetesConstants.StorageResourceName].ToString());
    }
}
