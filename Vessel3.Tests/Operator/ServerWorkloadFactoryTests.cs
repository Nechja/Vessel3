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
    }

    [Fact]
    public void CreateStatefulSet_BuildsExpectedSecurityContextAndEnvironment()
    {
        var statefulSet = ServerWorkloadFactory.CreateStatefulSet(TestServer, TestCredentials);

        Assert.Equal("prod-vessel", statefulSet.Metadata.Name);
        Assert.Equal("storage", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal(3, statefulSet.Spec.Replicas);
        Assert.Equal("prod-vessel", statefulSet.Spec.ServiceName);

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
