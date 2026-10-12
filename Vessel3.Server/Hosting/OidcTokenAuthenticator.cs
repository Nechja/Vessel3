namespace Vessel3.Server.Hosting;

internal sealed class OidcTokenAuthenticator(ITokenVerifier verifier, IIdentityRegistry registry) : ITokenAuthenticator
{
    public async Task<Result<CallerIdentity>> AuthenticateToken(string token, CancellationToken ct = default)
    {
        var verifyResult = await verifier.Verify(token, ct);
        if (!verifyResult.TryGetValue(out var verified, out var err))
            return err;

        var targetRole = verified.IsAdmin ? UserRole.Admin : UserRole.Member;
        User user;

        if (registry.FindUserByUsername(verified.Subject).TryGetValue(out var existingUser, out var findErr))
        {
            user = existingUser;
            if (verified.IsAdmin && user.Role != UserRole.Admin)
            {
                var updateResult = registry.UpdateUserRole(user.Id, UserRole.Admin);
                if (updateResult.TryGetError(out var updateErr))
                    return updateErr;
                user = user with { Role = UserRole.Admin };
            }
        }
        else if (findErr is NotFoundError)
        {
            var createResult = registry.CreateUser(verified.Subject, targetRole);
            if (!createResult.TryGetValue(out user!, out var createErr))
                return createErr;
        }
        else
        {
            return findErr;
        }

        return new CallerIdentity(user.Id, user.Username, user.Role, token);
    }
}
