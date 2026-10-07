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
/// </summary>
public sealed class VerifyLoginCodeHandler(
    IClientRepository clients,
    ILoginCodeRepository codes,
    IUserRepository users,
    IUserClientRepository userClients,
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

        var stored = await codes.FindLatestAsync(IdentityProvider.Email, email, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.StateAt(now) != LoginCodeState.Usable)
        {
            return Result.Failure<AuthTokensDto>(InvalidCode);
        }

        // The attempt is counted before the comparison, so a burst of parallel guesses is still limited to five.
        if (!await codes.TryClaimAttemptAsync(stored.Id, LoginCodeRules.MaxAttempts, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<AuthTokensDto>(InvalidCode);
        }

        var presented = LoginCodeRules.Hash(settings.CodeSecret, IdentityProvider.Email, email, command.Code);
        if (!stored.Matches(presented) || !await codes.TryConsumeAsync(stored.Id, now, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<AuthTokensDto>(InvalidCode);
        }

        var (user, isNew) = await users.GetOrCreateByIdentityAsync(IdentityProvider.Email, email, now, cancellationToken).ConfigureAwait(false);
        await userClients.RecordSignInAsync(user.Id, client.Id, now, cancellationToken).ConfigureAwait(false);

        var token = tokenIssuer.Issue(user, client, now);
        return Result.Success(new AuthTokensDto(token.Token, token.ExpiresUtc, isNew));
    }
}
