using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native;

internal interface ITokenAuthenticator
{
    Task<Result<CallerIdentity>> AuthenticateTokenAsync(string token, CancellationToken ct = default);
}
