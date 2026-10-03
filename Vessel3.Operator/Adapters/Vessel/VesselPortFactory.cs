using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Operator.Ports;

namespace Vessel3.Operator.Adapters.Vessel;

public sealed class VesselPortFactory(IKubernetesPort k8s) : IVesselPortFactory
{
    public async Task<Result<IVesselPort>> CreateForServer(string serverNamespace, string serverName, CancellationToken ct = default)
    {
        var secretName = $"{serverName}-admin-creds";
        var credsResult = await k8s.FetchServerCredentials(serverNamespace, secretName, ct);
        if (!credsResult.TryGetValue(out var creds, out var credsErr))
        {
            return credsErr;
        }

        var endpoint = $"http://{serverName}.{serverNamespace}.svc:9000";
        var client = new VesselClient(endpoint, creds.AccessKey, creds.SecretKey);
        return new VesselHttpAdapter(client);
    }
}
