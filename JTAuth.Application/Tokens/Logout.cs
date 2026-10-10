using FluentValidation;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Domain;

namespace JTAuth.Application.Tokens;

/// <summary>Ends the session the refresh token belongs to, so it can no longer be refreshed.</summary>
public sealed record LogoutCommand(string RefreshToken) : ICommand<Unit>;

public sealed class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator()
    {
        RuleFor(command => command.RefreshToken).NotEmpty().MaximumLength(RefreshTokenLimits.MaxPresentedLength);
    }
}

/// <summary>
/// Revokes the presented token's chain (which holds exactly one live token, the one presented). It succeeds whether or not the
/// token is known, so the answer says nothing about which tokens exist. Access tokens already issued last out their 15 minutes.
/// </summary>
public sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, TimeProvider clock) : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        var stored = await refreshTokens.FindByHashAsync(RefreshTokenRules.Hash(command.RefreshToken), cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            await refreshTokens.RevokeChainAsync(stored.FamilyId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
