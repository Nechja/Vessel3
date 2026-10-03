using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;

namespace Vessel3.Operator.Ports;

public interface IKubernetesPort
{
    Task<IReadOnlyList<ServerDeclaration>> ListServers(CancellationToken ct = default);
    Task<IReadOnlyList<BucketDeclaration>> ListBuckets(CancellationToken ct = default);
    Task<IReadOnlyList<UserDeclaration>> ListUsers(CancellationToken ct = default);

    Task<Result> UpdateServerStatus(ResourceIdentity id, ServerResourceStatus status, CancellationToken ct = default);
    Task<Result> UpdateBucketStatus(ResourceIdentity id, BucketResourceStatus status, CancellationToken ct = default);
    Task<Result> UpdateUserStatus(ResourceIdentity id, UserResourceStatus status, CancellationToken ct = default);

    Task<Result<ServerCredentials>> EnsureServerSecret(ResourceIdentity id, string secretName, CancellationToken ct = default);
    Task<Result<ServerCredentials>> FetchServerCredentials(ResourceIdentity id, string secretName, CancellationToken ct = default);

    Task<Result> ReconcileServerWorkload(ServerDeclaration server, ServerCredentials credentials, CancellationToken ct = default);
    Task<Result> WriteUserSecret(string @namespace, string secretName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}
