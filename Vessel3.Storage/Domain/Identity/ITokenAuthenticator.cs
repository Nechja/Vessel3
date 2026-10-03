using Vessel3.Primitives;

namespace Vessel3.Storage;

internal interface ITokenAuthenticator
{
    Task<Result<CallerIdentity>> AuthenticateToken(string token, CancellationToken ct = default);
}
