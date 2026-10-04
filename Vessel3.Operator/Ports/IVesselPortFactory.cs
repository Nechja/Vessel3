using Vessel3.Operator.Domain.Models;
using Vessel3.Primitives;

namespace Vessel3.Operator.Ports;

public interface IVesselPortFactory
{
    Task<Result<IVesselPort>> CreateForServer(ResourceIdentity serverId, CancellationToken ct = default);
}
