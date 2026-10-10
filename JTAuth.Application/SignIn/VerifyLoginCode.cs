using FluentValidation;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>Finishes a sign-in with the emailed code. An address with no account gets one.</summary>
public sealed record VerifyLoginCodeCommand(string ClientId, string Email, string Code) : ICommand<AuthTokensDto>;

public sealed class VerifyLoginCodeValidator : AbstractValidator<VerifyLoginCodeCommand>
{
    public VerifyLoginCodeValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty().MaximumLength(50);
        RuleFor(command => command.Email)
            .Must(email => EmailAddress.TryNormalize(email, out _))
            .WithMessage("Enter a valid email address.");
        RuleFor(command => command.Code)
            .Must(LoginCodeRules.IsWellFormed)
            .WithMessage($"The code is {LoginCodeRules.CodeLength} digits.");
    }
}

/// <summary>
/// Every way the code can be wrong (no code for the address, wrong digits, expired, used, replaced, out of attempts)
/// gets the same answer, so the response says nothing about whether the address has an account or which rule failed.
/// A successful sign-in starts a new chain of refresh tokens for this person and app.
/// </summary>
public sealed class VerifyLoginCodeHandler(
    IClientRepository clients,
    ILoginCodeRepository codes,
    IUserRepository users,
    IUserClientRepository userClients,
    IRefreshTokenRepository refreshTokens,
    IAccessTokenIssuer tokenIssuer,
    JTAuthSettings settings,
    TimeProvider clock) : ICommandHandler<VerifyLoginCodeCommand, AuthTokensDto>
{
    public const string InvalidCodeCode = "invalid_code";

    private static readonly ResultError InvalidCode =
        ResultError.Unauthorized(InvalidCodeCode, "The code is wrong or has expired. Request a new one.");

    public async Task<Result<AuthTokensDto>> HandleAsync(VerifyLoginCodeCommand command, CancellationToken cancellationToken)
    {
        var (client, error) = await ClientCheck.FindUsableAsync(clients, command.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<AuthTokensDto>(error!);
        }

        var email = EmailAddress.Normalize(command.Email);
        var now = clock.GetUtcNow();

        if (!await LoginCodeFlow.RedeemAsync(codes, settings, now, email, command.Code, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<AuthTokensDto>(InvalidCode);
        }

        var (user, isNew) = await users.GetOrCreateByIdentityAsync(IdentityProvider.Email, email, now, cancellationToken).ConfigureAwait(false);
        await userClients.RecordSignInAsync(user.Id, client.Id, now, cancellationToken).ConfigureAwait(false);

        var (refreshToken, refreshHash) = RefreshTokenRules.Generate();
        var chain = RefreshToken.StartChain(user.Id, client.Id, refreshHash, now);
        await refreshTokens.AddAsync(chain, cancellationToken).ConfigureAwait(false);

        var access = tokenIssuer.Issue(user, client, now);
        return Result.Success(new AuthTokensDto(access.Token, access.ExpiresUtc, refreshToken, chain.ExpiresUtc, isNew));
    }
}
