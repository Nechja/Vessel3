using Vessel3.Primitives;

namespace Vessel3.Operator.Ports;

public interface IVesselPortFactory
{
    Task<Result<IVesselPort>> CreateForServer(string serverNamespace, string serverName, CancellationToken ct = default);
}
