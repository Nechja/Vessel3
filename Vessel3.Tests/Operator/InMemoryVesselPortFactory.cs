using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Tests.Operator;

public sealed class InMemoryVesselPortFactory : IVesselPortFactory
{
    public InMemoryVesselPort Port { get; } = new();
    public bool ShouldFailConnection { get; set; }

    public Task<Result<IVesselPort>> CreateForServer(ResourceIdentity serverId, CancellationToken ct = default)
    {
        if (ShouldFailConnection)
        {
            return Task.FromResult<Result<IVesselPort>>(new Error("ConnectionError", $"Cannot connect to {serverId.QualifiedName}"));
        }

        return Task.FromResult<Result<IVesselPort>>(Port);
    }
}
