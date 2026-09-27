using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Server.Oidc;
using Vessel3.Storage;

namespace Vessel3.Server.Hosting;

internal sealed class OidcTokenAuthenticator(ITokenVerifier verifier, IIdentityRegistry registry) : ITokenAuthenticator
{
    public async Task<Result<CallerIdentity>> AuthenticateTokenAsync(string token, CancellationToken ct = default)
    {
        var verifyResult = await verifier.Verify(token, ct);
        if (!verifyResult.TryGetValue(out var verified, out var err))
            return err;

        var userResult = registry.FindUserByUsername(verified.Subject);
        if (!userResult.TryGetValue(out var user, out var findErr))
            return findErr;

        if (user is null)
        {
            var createResult = registry.CreateUser(verified.Subject, UserRole.Member);
            if (!createResult.TryGetValue(out user, out var createErr))
                return createErr;
        }

        return new CallerIdentity(user.Id, user.Username, user.Role, token);
    }
}
