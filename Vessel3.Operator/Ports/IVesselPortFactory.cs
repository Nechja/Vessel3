using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;

namespace Vessel3.Operator.Ports;

public interface IVesselPortFactory
{
    Task<Result<IVesselPort>> CreateForServer(ResourceIdentity serverId, CancellationToken ct = default);
}
