using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Adapters.Vessel;

public sealed class VesselPortFactory(IKubernetesPort k8s) : IVesselPortFactory
{
    public async Task<Result<IVesselPort>> CreateForServer(ResourceIdentity serverId, CancellationToken ct = default)
    {
        var secretName = $"{serverId.Name}-admin-creds";
        var credsResult = await k8s.FetchServerCredentials(serverId, secretName, ct);
        if (!credsResult.TryGetValue(out var creds, out var credsErr))
        {
            return credsErr;
        }

        var endpoint = $"http://{serverId.Name}.{serverId.Namespace}.svc:9000";
        var client = new VesselClient(endpoint, creds.AccessKey, creds.SecretKey);
        return new VesselHttpAdapter(client);
    }
}
