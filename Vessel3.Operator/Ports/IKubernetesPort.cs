using Vessel3.Primitives;
using Vessel3.Operator.Adapters.Kubernetes.Models;

namespace Vessel3.Operator.Ports;

public interface IKubernetesPort
{
    Task<IReadOnlyList<VesselServerCustomResource>> ListServers(CancellationToken ct = default);
    Task<IReadOnlyList<VesselBucketCustomResource>> ListBuckets(CancellationToken ct = default);
    Task<IReadOnlyList<VesselUserCustomResource>> ListUsers(CancellationToken ct = default);

    Task<Result> UpdateServerStatus(string @namespace, string name, VesselServerStatus status, CancellationToken ct = default);
    Task<Result> UpdateBucketStatus(string @namespace, string name, VesselBucketStatus status, CancellationToken ct = default);
    Task<Result> UpdateUserStatus(string @namespace, string name, VesselUserStatus status, CancellationToken ct = default);

    Task<Result<ServerCredentials>> EnsureServerSecret(string @namespace, string secretName, CancellationToken ct = default);
    Task<Result<ServerCredentials>> FetchServerCredentials(string @namespace, string secretName, CancellationToken ct = default);

    Task<Result> ReconcileServerWorkload(VesselServerCustomResource server, ServerCredentials credentials, CancellationToken ct = default);
    Task<Result> WriteUserSecret(string @namespace, string secretName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}
