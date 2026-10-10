using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>What every way of signing in ends with: a new chain of refresh tokens and a first access token, for one person and one app.</summary>
internal static class SessionIssuer
{
    public static async Task<AuthTokensDto> StartAsync(
        IRefreshTokenRepository refreshTokens, IAccessTokenIssuer tokenIssuer, User user, Client client, bool isNewUser, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (refreshToken, refreshHash) = RefreshTokenRules.Generate();
        var chain = RefreshToken.StartChain(user.Id, client.Id, refreshHash, now);
        await refreshTokens.AddAsync(chain, cancellationToken).ConfigureAwait(false);

        var access = tokenIssuer.Issue(user, client, now);
        return new AuthTokensDto(access.Token, access.ExpiresUtc, refreshToken, chain.ExpiresUtc, isNewUser);
    }
}
