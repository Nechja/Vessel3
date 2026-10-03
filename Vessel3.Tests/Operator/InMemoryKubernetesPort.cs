using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Tests.Operator;

public sealed class InMemoryKubernetesPort : IKubernetesPort
{
    public List<VesselServerCustomResource> Servers { get; } = [];
    public List<VesselBucketCustomResource> Buckets { get; } = [];
    public List<VesselUserCustomResource> Users { get; } = [];

    public Dictionary<(string Ns, string Name), VesselServerStatus> ServerStatuses { get; } = [];
    public Dictionary<(string Ns, string Name), VesselBucketStatus> BucketStatuses { get; } = [];
    public Dictionary<(string Ns, string Name), VesselUserStatus> UserStatuses { get; } = [];

    public Dictionary<(string Ns, string Name), ServerCredentials> Secrets { get; } = [];
    public Dictionary<(string Ns, string Name), IReadOnlyDictionary<string, string>> UserSecrets { get; } = [];

    public bool ShouldFailSecretCreation { get; set; }
    public bool ShouldFailWorkloadReconciliation { get; set; }
    public bool ShouldFailUserSecretWrite { get; set; }

    public Task<IReadOnlyList<VesselServerCustomResource>> ListServers(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VesselServerCustomResource>>(Servers);

    public Task<IReadOnlyList<VesselBucketCustomResource>> ListBuckets(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VesselBucketCustomResource>>(Buckets);

    public Task<IReadOnlyList<VesselUserCustomResource>> ListUsers(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VesselUserCustomResource>>(Users);

    public Task<Result> UpdateServerStatus(string @namespace, string name, VesselServerStatus status, CancellationToken ct = default)
    {
        ServerStatuses[(@namespace, name)] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> UpdateBucketStatus(string @namespace, string name, VesselBucketStatus status, CancellationToken ct = default)
    {
        BucketStatuses[(@namespace, name)] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> UpdateUserStatus(string @namespace, string name, VesselUserStatus status, CancellationToken ct = default)
    {
        UserStatuses[(@namespace, name)] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result<ServerCredentials>> EnsureServerSecret(string @namespace, string secretName, CancellationToken ct = default)
    {
        if (ShouldFailSecretCreation)
        {
            return Task.FromResult<Result<ServerCredentials>>(new Error("SecretError", "Failed to ensure secret"));
        }

        if (!Secrets.TryGetValue((@namespace, secretName), out var creds))
        {
            creds = new ServerCredentials("test-access", "test-secret");
            Secrets[(@namespace, secretName)] = creds;
        }

        return Task.FromResult<Result<ServerCredentials>>(creds);
    }

    public Task<Result<ServerCredentials>> FetchServerCredentials(string @namespace, string secretName, CancellationToken ct = default)
    {
        if (Secrets.TryGetValue((@namespace, secretName), out var creds))
        {
            return Task.FromResult<Result<ServerCredentials>>(creds);
        }

        return Task.FromResult<Result<ServerCredentials>>(new Error("SecretNotFound", $"Secret {secretName} not found"));
    }

    public Task<Result> ReconcileServerWorkload(VesselServerCustomResource server, ServerCredentials credentials, CancellationToken ct = default)
    {
        if (ShouldFailWorkloadReconciliation)
        {
            return Task.FromResult<Result>(new Error("WorkloadError", "Failed to reconcile workload"));
        }

        return Task.FromResult(Result.Ok);
    }

    public Task<Result> WriteUserSecret(string @namespace, string secretName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (ShouldFailUserSecretWrite)
        {
            return Task.FromResult<Result>(new Error("UserSecretError", "Failed to write user secret"));
        }

        UserSecrets[(@namespace, secretName)] = data;
        return Task.FromResult(Result.Ok);
    }
}
