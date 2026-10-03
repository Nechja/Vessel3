using Vessel3.Primitives;

namespace Vessel3.Storage;

internal interface ITokenAuthenticator
{
    Task<Result<CallerIdentity>> AuthenticateTokenAsync(string token, CancellationToken ct = default);
}
