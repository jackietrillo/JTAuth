using FluentValidation;
using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Tokens;

/// <summary>Exchanges a refresh token for a new access token and a new refresh token.</summary>
public sealed record RefreshTokensCommand(string ClientId, string RefreshToken) : ICommand<AuthTokensDto>;

public sealed class RefreshTokensValidator : AbstractValidator<RefreshTokensCommand>
{
    public RefreshTokensValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty().MaximumLength(50);
        RuleFor(command => command.RefreshToken).NotEmpty().MaximumLength(RefreshTokenLimits.MaxPresentedLength);
    }
}

/// <summary>Bounds what is accepted as a refresh token, so an oversized value is refused before it is hashed.</summary>
internal static class RefreshTokenLimits
{
    public const int MaxPresentedLength = 200;
}

/// <summary>
/// Every refresh token is single use: refreshing revokes it and returns the next token in the same chain. A token that is
/// presented after it was already used, or after a logout, means a copy may have leaked, so the whole chain it belongs to is
/// ended and the person signs in again with a code. Every way a refresh can fail gets the same answer.
/// </summary>
public sealed class RefreshTokensHandler(
    IClientRepository clients,
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    IAccessTokenIssuer tokenIssuer,
    TimeProvider clock) : ICommandHandler<RefreshTokensCommand, AuthTokensDto>
{
    public const string InvalidRefreshTokenCode = "invalid_refresh_token";

    private static readonly ResultError InvalidRefreshToken =
        ResultError.Unauthorized(InvalidRefreshTokenCode, "The refresh token is not valid. Sign in again.");

    public async Task<Result<AuthTokensDto>> HandleAsync(RefreshTokensCommand command, CancellationToken cancellationToken)
    {
        var (client, error) = await ClientCheck.FindUsableAsync(clients, command.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<AuthTokensDto>(error!);
        }

        var now = clock.GetUtcNow();
        var stored = await refreshTokens.FindByHashAsync(RefreshTokenRules.Hash(command.RefreshToken), cancellationToken).ConfigureAwait(false);

        // A token belongs to the app it was issued to; presenting it to another app is a mistake, not evidence of a leak.
        if (stored is null || stored.ClientId != client.Id)
        {
            return Result.Failure<AuthTokensDto>(InvalidRefreshToken);
        }

        switch (stored.StateAt(now))
        {
            case RefreshTokenState.Revoked:
                await refreshTokens.RevokeChainAsync(stored.FamilyId, now, cancellationToken).ConfigureAwait(false);
                return Result.Failure<AuthTokensDto>(InvalidRefreshToken);
            case RefreshTokenState.Expired:
                return Result.Failure<AuthTokensDto>(InvalidRefreshToken);
        }

        var user = await users.FindByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<AuthTokensDto>(InvalidRefreshToken);
        }

        var (refreshToken, refreshHash) = RefreshTokenRules.Generate();
        var next = stored.Next(refreshHash, now);
        if (!await refreshTokens.RotateAsync(stored.Id, next, now, cancellationToken).ConfigureAwait(false))
        {
            // Another request used this token between the check above and now: the same as presenting it twice.
            await refreshTokens.RevokeChainAsync(stored.FamilyId, now, cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthTokensDto>(InvalidRefreshToken);
        }

        // The person is read afresh, so a changed display name is in the new access token.
        var access = tokenIssuer.Issue(user, client, now);
        return Result.Success(new AuthTokensDto(access.Token, access.ExpiresUtc, refreshToken, next.ExpiresUtc, IsNewUser: false));
    }
}
