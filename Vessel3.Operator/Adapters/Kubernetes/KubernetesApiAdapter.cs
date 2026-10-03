using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Adapters.Serialization;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Adapters.Kubernetes;

public sealed class KubernetesApiAdapter(IKubernetes client) : IKubernetesPort
{
    public async Task<IReadOnlyList<VesselServerCustomResource>> ListServers(CancellationToken ct = default)
    {
        try
        {
            var raw = await client.CustomObjects.ListClusterCustomObjectAsync(
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                KubernetesConstants.ServerPlural,
                cancellationToken: ct);

            return ExtractItems(raw, OperatorJsonContext.Default.VesselServerCustomResource);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<VesselBucketCustomResource>> ListBuckets(CancellationToken ct = default)
    {
        try
        {
            var raw = await client.CustomObjects.ListClusterCustomObjectAsync(
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                KubernetesConstants.BucketPlural,
                cancellationToken: ct);

            return ExtractItems(raw, OperatorJsonContext.Default.VesselBucketCustomResource);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<VesselUserCustomResource>> ListUsers(CancellationToken ct = default)
    {
        try
        {
            var raw = await client.CustomObjects.ListClusterCustomObjectAsync(
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                KubernetesConstants.UserPlural,
                cancellationToken: ct);

            return ExtractItems(raw, OperatorJsonContext.Default.VesselUserCustomResource);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<Result> UpdateServerStatus(string @namespace, string name, VesselServerStatus status, CancellationToken ct = default)
    {
        try
        {
            var patchDoc = new ServerStatusPatch(status);
            var patchJson = JsonSerializer.Serialize(patchDoc, OperatorJsonContext.Default.ServerStatusPatch);
            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

            await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
                patch,
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                @namespace,
                KubernetesConstants.ServerPlural,
                name,
                cancellationToken: ct);

            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result> UpdateBucketStatus(string @namespace, string name, VesselBucketStatus status, CancellationToken ct = default)
    {
        try
        {
            var patchDoc = new BucketStatusPatch(status);
            var patchJson = JsonSerializer.Serialize(patchDoc, OperatorJsonContext.Default.BucketStatusPatch);
            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

            await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
                patch,
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                @namespace,
                KubernetesConstants.BucketPlural,
                name,
                cancellationToken: ct);

            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result> UpdateUserStatus(string @namespace, string name, VesselUserStatus status, CancellationToken ct = default)
    {
        try
        {
            var patchDoc = new UserStatusPatch(status);
            var patchJson = JsonSerializer.Serialize(patchDoc, OperatorJsonContext.Default.UserStatusPatch);
            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

            await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
                patch,
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                @namespace,
                KubernetesConstants.UserPlural,
                name,
                cancellationToken: ct);

            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result<ServerCredentials>> EnsureServerSecret(string @namespace, string secretName, CancellationToken ct = default)
    {
        try
        {
            var existing = await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
            if (existing.Data is not null
                && existing.Data.TryGetValue(KubernetesConstants.AccessKeyField, out var akBytes)
                && existing.Data.TryGetValue(KubernetesConstants.SecretKeyField, out var skBytes))
            {
                var ak = Encoding.UTF8.GetString(akBytes);
                var sk = Encoding.UTF8.GetString(skBytes);
                return new ServerCredentials(ak, sk);
            }
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        var newAccessKey = GenerateRandomKey("V3AK", 16);
        var newSecretKey = GenerateRandomSecret(40);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = @namespace },
            Type = "Opaque",
            StringData = new Dictionary<string, string>
            {
                [KubernetesConstants.AccessKeyField] = newAccessKey,
                [KubernetesConstants.SecretKeyField] = newSecretKey
            }
        };

        try
        {
            await client.CoreV1.CreateNamespacedSecretAsync(secret, @namespace, cancellationToken: ct);
            return new ServerCredentials(newAccessKey, newSecretKey);
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result<ServerCredentials>> FetchServerCredentials(string @namespace, string secretName, CancellationToken ct = default)
    {
        try
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
            if (secret.Data is not null
                && secret.Data.TryGetValue(KubernetesConstants.AccessKeyField, out var akBytes)
                && secret.Data.TryGetValue(KubernetesConstants.SecretKeyField, out var skBytes))
            {
                var ak = Encoding.UTF8.GetString(akBytes);
                var sk = Encoding.UTF8.GetString(skBytes);
                return new ServerCredentials(ak, sk);
            }

            return new Error("NotFound", $"Secret {secretName} is missing credentials keys");
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result> ReconcileServerWorkload(VesselServerCustomResource server, ServerCredentials credentials, CancellationToken ct = default)
    {
        var ns = string.IsNullOrEmpty(server.Metadata.Namespace) ? "default" : server.Metadata.Namespace;
        var name = server.Metadata.Name;
        var port = server.Spec.Service.Port;
        var labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "vessel3", ["app.kubernetes.io/instance"] = name };

        var service = new V1Service
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1ServiceSpec
            {
                Type = server.Spec.Service.Type,
                Selector = labels,
                Ports = [new V1ServicePort { Name = "http-s3", Port = port, TargetPort = port }]
            }
        };

        var statefulSet = new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1StatefulSetSpec
            {
                ServiceName = name,
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        SecurityContext = new V1PodSecurityContext { FsGroup = 1654, RunAsUser = 1654, RunAsNonRoot = true },
                        Containers = [
                            new V1Container
                            {
                                Name = "vessel3",
                                Image = server.Spec.Image,
                                ImagePullPolicy = server.Spec.ImagePullPolicy,
                                Ports = [new V1ContainerPort { ContainerPort = port, Name = "http" }],
                                Env = [
                                    new V1EnvVar { Name = "ASPNETCORE_URLS", Value = $"http://0.0.0.0:{port}" },
                                    new V1EnvVar { Name = "VESSEL3_DATA", Value = "/data" },
                                    new V1EnvVar { Name = "VESSEL3_ACCESS_KEY", Value = credentials.AccessKey },
                                    new V1EnvVar { Name = "VESSEL3_SECRET_KEY", Value = credentials.SecretKey },
                                    new V1EnvVar { Name = "VESSEL3_REGION", Value = "us-east-1" }
                                ],
                                VolumeMounts = [new V1VolumeMount { Name = "data", MountPath = "/data" }]
                            }
                        ]
                    }
                },
                VolumeClaimTemplates = [
                    new V1PersistentVolumeClaim
                    {
                        Metadata = new V1ObjectMeta { Name = "data" },
                        Spec = new V1PersistentVolumeClaimSpec
                        {
                            AccessModes = ["ReadWriteOnce"],
                            StorageClassName = server.Spec.Storage.StorageClassName,
                            Resources = new V1VolumeResourceRequirements
                            {
                                Requests = new Dictionary<string, ResourceQuantity>
                                {
                                    ["storage"] = new(server.Spec.Storage.Size)
                                }
                            }
                        }
                    }
                ]
            }
        };

        try
        {
            await ApplyService(ns, service, ct);
            await ApplyStatefulSet(ns, statefulSet, ct);
            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result> WriteUserSecret(string @namespace, string secretName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = @namespace },
            Type = "Opaque",
            StringData = data.ToDictionary(k => k.Key, v => v.Value)
        };

        try
        {
            try
            {
                await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
                await client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, @namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await client.CoreV1.CreateNamespacedSecretAsync(secret, @namespace, cancellationToken: ct);
            }

            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    private async Task ApplyService(string @namespace, V1Service service, CancellationToken ct)
    {
        try
        {
            await client.CoreV1.ReadNamespacedServiceAsync(service.Metadata.Name, @namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CoreV1.CreateNamespacedServiceAsync(service, @namespace, cancellationToken: ct);
        }
    }

    private async Task ApplyStatefulSet(string @namespace, V1StatefulSet statefulSet, CancellationToken ct)
    {
        try
        {
            await client.AppsV1.ReadNamespacedStatefulSetAsync(statefulSet.Metadata.Name, @namespace, cancellationToken: ct);
            await client.AppsV1.ReplaceNamespacedStatefulSetAsync(statefulSet, statefulSet.Metadata.Name, @namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, @namespace, cancellationToken: ct);
        }
    }

    private static IReadOnlyList<T> ExtractItems<T>(object raw, JsonTypeInfo<T> typeInfo) =>
        raw is JsonElement element ? ParseElementItems(element, typeInfo) : [];

    private static IReadOnlyList<T> ParseElementItems<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<T>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            var parsed = JsonSerializer.Deserialize(item.GetRawText(), typeInfo);
            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    private static string GenerateRandomKey(string prefix, int randomLength)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buffer = new char[randomLength];
        for (var i = 0; i < randomLength; i++)
        {
            buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return prefix + new string(buffer);
    }

    private static string GenerateRandomSecret(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return new string(buffer);
    }
}
