using Vessel3.Client;
using Vessel3.Operator.Adapters.Kubernetes;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Vessel3.Primitives;

namespace Vessel3.Operator.Adapters.Vessel;

public sealed class VesselPortFactory(IKubernetesPort k8s) : IVesselPortFactory
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<Result<IVesselPort>> CreateForServer(ResourceIdentity serverId, CancellationToken ct = default)
    {
        var secretName = $"{serverId.Name}{KubernetesConstants.AdminCredsSuffix}";
        var credsResult = await k8s.FetchServerCredentials(serverId, secretName, ct);
        if (!credsResult.TryGetValue(out var creds, out var credsErr))
        {
            return credsErr;
        }

        var endpoint = serverId.BuildClusterEndpoint();
        var http = new HttpClient(SharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri($"{endpoint.TrimEnd('/')}/")
        };
        var client = new VesselClient(http, new VesselClientOptions(endpoint, creds.AccessKey, creds.SecretKey), disposeHttpClient: true);
        return new VesselHttpAdapter(client);
    }
}
