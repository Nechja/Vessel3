namespace Vessel3.Server.Hosting;

internal sealed class OidcTokenAuthenticator(ITokenVerifier verifier, IIdentityRegistry registry) : ITokenAuthenticator
{
    public async Task<Result<CallerIdentity>> AuthenticateToken(string token, CancellationToken ct = default)
    {
        var verifyResult = await verifier.Verify(token, ct);
        if (!verifyResult.TryGetValue(out var verified, out var err))
            return err;

        var userResult = registry.FindUserByUsername(verified.Subject);
        if (!userResult.TryGetValue(out var user, out var findErr))
            return findErr;

        var targetRole = verified.IsAdmin ? UserRole.Admin : UserRole.Member;

        if (user is null)
        {
            var createResult = registry.CreateUser(verified.Subject, targetRole);
            if (!createResult.TryGetValue(out user, out var createErr))
                return createErr;
        }
        else if (verified.IsAdmin && user.Role != UserRole.Admin)
        {
            var updateResult = registry.UpdateUserRole(user.Id, UserRole.Admin);
            if (updateResult.TryGetError(out var updateErr))
                return updateErr;
            user = user with { Role = UserRole.Admin };
        }

        return new CallerIdentity(user.Id, user.Username, user.Role, token);
    }
}
