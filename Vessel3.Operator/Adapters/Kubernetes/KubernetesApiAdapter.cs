using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Vessel3.Operator.Adapters.Serialization;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Vessel3.Primitives;

namespace Vessel3.Operator.Adapters.Kubernetes;

public sealed class KubernetesApiAdapter(IKubernetes client) : IKubernetesPort
{
    public Task<IReadOnlyList<ServerDeclaration>> ListServers(CancellationToken ct = default) =>
        ListCustomObjects(KubernetesConstants.ServerPlural, OperatorJsonContext.Default.VesselServerCustomResource, KubernetesModelMapper.ToDeclaration, ct);

    public Task<IReadOnlyList<BucketDeclaration>> ListBuckets(CancellationToken ct = default) =>
        ListCustomObjects(KubernetesConstants.BucketPlural, OperatorJsonContext.Default.VesselBucketCustomResource, KubernetesModelMapper.ToDeclaration, ct);

    public Task<IReadOnlyList<UserDeclaration>> ListUsers(CancellationToken ct = default) =>
        ListCustomObjects(KubernetesConstants.UserPlural, OperatorJsonContext.Default.VesselUserCustomResource, KubernetesModelMapper.ToDeclaration, ct);

    public Task<IReadOnlyList<WebhookDeclaration>> ListWebhooks(CancellationToken ct = default) =>
        ListCustomObjects(KubernetesConstants.WebhookPlural, OperatorJsonContext.Default.VesselWebhookCustomResource, KubernetesModelMapper.ToDeclaration, ct);

    public Task<Result> UpdateServerStatus(ResourceIdentity id, ServerResourceStatus status, CancellationToken ct = default) =>
        PatchCustomObjectStatus(id, KubernetesConstants.ServerPlural, KubernetesModelMapper.ToPatch(status), OperatorJsonContext.Default.ServerStatusPatch, ct);

    public Task<Result> UpdateBucketStatus(ResourceIdentity id, BucketResourceStatus status, CancellationToken ct = default) =>
        PatchCustomObjectStatus(id, KubernetesConstants.BucketPlural, KubernetesModelMapper.ToPatch(status), OperatorJsonContext.Default.BucketStatusPatch, ct);

    public Task<Result> UpdateUserStatus(ResourceIdentity id, UserResourceStatus status, CancellationToken ct = default) =>
        PatchCustomObjectStatus(id, KubernetesConstants.UserPlural, KubernetesModelMapper.ToPatch(status), OperatorJsonContext.Default.UserStatusPatch, ct);

    public Task<Result> UpdateWebhookStatus(ResourceIdentity id, WebhookResourceStatus status, CancellationToken ct = default) =>
        PatchCustomObjectStatus(id, KubernetesConstants.WebhookPlural, KubernetesModelMapper.ToPatch(status), OperatorJsonContext.Default.WebhookStatusPatch, ct);

    public async Task<Result<string?>> FetchSecretValue(string @namespace, string secretName, string key, CancellationToken ct = default)
    {
        try
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
            if (secret.Data is not null && secret.Data.TryGetValue(key, out var bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            return (string?)null;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new Error("NotFound", $"Secret {secretName} not found in namespace {@namespace}");
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    private async Task<IReadOnlyList<TDeclaration>> ListCustomObjects<TResource, TDeclaration>(
        string plural,
        JsonTypeInfo<TResource> typeInfo,
        Func<TResource, TDeclaration> mapper,
        CancellationToken ct)
    {
        try
        {
            var raw = await client.CustomObjects.ListClusterCustomObjectAsync(
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                plural,
                cancellationToken: ct);

            var items = ExtractItems(raw, typeInfo);
            return [.. items.Select(mapper)];
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    private async Task<Result> PatchCustomObjectStatus<TPatch>(
        ResourceIdentity id,
        string plural,
        TPatch patchDoc,
        JsonTypeInfo<TPatch> typeInfo,
        CancellationToken ct)
    {
        try
        {
            var patchJson = JsonSerializer.Serialize(patchDoc, typeInfo);
            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

            await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
                patch,
                KubernetesConstants.Group,
                KubernetesConstants.Version,
                id.Namespace,
                plural,
                id.Name,
                cancellationToken: ct);

            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result<ServerCredentials>> EnsureServerSecret(ResourceIdentity id, string secretName, CancellationToken ct = default)
    {
        var existing = await TryReadServerCredentials(id.Namespace, secretName, ct);
        if (existing is not null)
        {
            return existing;
        }

        var newAccessKey = GenerateRandomKey("V3AK", 16);
        var newSecretKey = GenerateRandomSecret(40);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = id.Namespace },
            Type = KubernetesConstants.SecretTypeOpaque,
            StringData = new Dictionary<string, string>
            {
                [KubernetesConstants.AccessKeyField] = newAccessKey,
                [KubernetesConstants.SecretKeyField] = newSecretKey
            }
        };

        try
        {
            await client.CoreV1.CreateNamespacedSecretAsync(secret, id.Namespace, cancellationToken: ct);
            return new ServerCredentials(newAccessKey, newSecretKey);
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result<ServerCredentials>> FetchServerCredentials(ResourceIdentity id, string secretName, CancellationToken ct = default)
    {
        try
        {
            var creds = await TryReadServerCredentials(id.Namespace, secretName, ct);
            return creds is not null
                ? creds
                : new Error("NotFound", $"Secret {secretName} is missing credentials keys");
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    public async Task<Result> ReconcileServerWorkload(ServerDeclaration server, ServerCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var service = ServerWorkloadFactory.CreateService(server);
            var statefulSet = ServerWorkloadFactory.CreateStatefulSet(server, credentials);

            await ApplyService(server.Identity.Namespace, service, ct);
            await ApplyStatefulSet(server.Identity.Namespace, statefulSet, ct);

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
            Type = KubernetesConstants.SecretTypeOpaque,
            StringData = data.ToDictionary(k => k.Key, v => v.Value)
        };

        try
        {
            await UpsertSecret(@namespace, secretName, secret, ct);
            return Result.Ok;
        }
        catch (Exception ex)
        {
            return new Error("Unexpected", ex.Message);
        }
    }

    private async Task<ServerCredentials?> TryReadServerCredentials(string @namespace, string secretName, CancellationToken ct)
    {
        try
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
            if (secret.Data is not null
                && secret.Data.TryGetValue(KubernetesConstants.AccessKeyField, out var akBytes)
                && secret.Data.TryGetValue(KubernetesConstants.SecretKeyField, out var skBytes))
            {
                return new ServerCredentials(Encoding.UTF8.GetString(akBytes), Encoding.UTF8.GetString(skBytes));
            }
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        return null;
    }

    private async Task UpsertSecret(string @namespace, string secretName, V1Secret secret, CancellationToken ct)
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
