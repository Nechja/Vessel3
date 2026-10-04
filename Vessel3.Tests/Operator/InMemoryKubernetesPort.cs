using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Tests.Operator;

public sealed class InMemoryKubernetesPort : IKubernetesPort
{
    public List<ServerDeclaration> Servers { get; } = [];
    public List<BucketDeclaration> Buckets { get; } = [];
    public List<UserDeclaration> Users { get; } = [];

    public Dictionary<ResourceIdentity, ServerResourceStatus> ServerStatuses { get; } = [];
    public Dictionary<ResourceIdentity, BucketResourceStatus> BucketStatuses { get; } = [];
    public Dictionary<ResourceIdentity, UserResourceStatus> UserStatuses { get; } = [];

    public Dictionary<(string Ns, string Name), ServerCredentials> Secrets { get; } = [];
    public Dictionary<(string Ns, string Name), IReadOnlyDictionary<string, string>> UserSecrets { get; } = [];

    public bool ShouldFailSecretCreation { get; set; }
    public bool ShouldFailWorkloadReconciliation { get; set; }
    public bool ShouldFailUserSecretWrite { get; set; }

    public Task<IReadOnlyList<ServerDeclaration>> ListServers(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ServerDeclaration>>(Servers);

    public Task<IReadOnlyList<BucketDeclaration>> ListBuckets(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BucketDeclaration>>(Buckets);

    public Task<IReadOnlyList<UserDeclaration>> ListUsers(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserDeclaration>>(Users);

    public Task<Result> UpdateServerStatus(ResourceIdentity id, ServerResourceStatus status, CancellationToken ct = default)
    {
        ServerStatuses[id] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> UpdateBucketStatus(ResourceIdentity id, BucketResourceStatus status, CancellationToken ct = default)
    {
        BucketStatuses[id] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> UpdateUserStatus(ResourceIdentity id, UserResourceStatus status, CancellationToken ct = default)
    {
        UserStatuses[id] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result<ServerCredentials>> EnsureServerSecret(ResourceIdentity id, string secretName, CancellationToken ct = default)
    {
        if (ShouldFailSecretCreation)
        {
            return Task.FromResult<Result<ServerCredentials>>(new Error("SecretError", "Failed to ensure secret"));
        }

        if (!Secrets.TryGetValue((id.Namespace, secretName), out var creds))
        {
            creds = new ServerCredentials("test-access", "test-secret");
            Secrets[(id.Namespace, secretName)] = creds;
        }

        return Task.FromResult<Result<ServerCredentials>>(creds);
    }

    public Task<Result<ServerCredentials>> FetchServerCredentials(ResourceIdentity id, string secretName, CancellationToken ct = default)
    {
        if (Secrets.TryGetValue((id.Namespace, secretName), out var creds))
        {
            return Task.FromResult<Result<ServerCredentials>>(creds);
        }

        return Task.FromResult<Result<ServerCredentials>>(new Error("SecretNotFound", $"Secret {secretName} not found"));
    }

    public Task<Result> ReconcileServerWorkload(ServerDeclaration server, ServerCredentials credentials, CancellationToken ct = default)
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
