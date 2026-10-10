using FluentValidation;
using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Profile;

/// <summary>Finishes changing the sign-in email: the code sent to the new address proves it is the person's.</summary>
public sealed record ConfirmEmailChangeCommand(Guid UserId, string NewEmail, string Code) : ICommand<ProfileDto>;

public sealed class ConfirmEmailChangeValidator : AbstractValidator<ConfirmEmailChangeCommand>
{
    public ConfirmEmailChangeValidator()
    {
        RuleFor(command => command.NewEmail)
            .Must(email => EmailAddress.TryNormalize(email, out _))
            .WithMessage("Enter a valid email address.");
        RuleFor(command => command.Code)
            .Must(LoginCodeRules.IsWellFormed)
            .WithMessage($"The code is {LoginCodeRules.CodeLength} digits.");
    }
}

/// <summary>
/// The old address stops being a way to sign in only once the code for the new one is accepted. Signing in with the old address
/// afterwards starts a new account, since no account owns it any more. Access tokens already issued last out their 15 minutes.
/// </summary>
public sealed class ConfirmEmailChangeHandler(
    IUserRepository users,
    ILoginCodeRepository codes,
    JTAuthSettings settings,
    TimeProvider clock) : ICommandHandler<ConfirmEmailChangeCommand, ProfileDto>
{
    public const string EmailInUseCode = "email_in_use";

    public async Task<Result<ProfileDto>> HandleAsync(ConfirmEmailChangeCommand command, CancellationToken cancellationToken)
    {
        var newEmail = EmailAddress.Normalize(command.NewEmail);
        var now = clock.GetUtcNow();

        if (!await LoginCodeFlow.RedeemAsync(codes, settings, now, newEmail, command.Code, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<ProfileDto>(ResultError.Validation(VerifyLoginCodeHandler.InvalidCodeCode, "The code is wrong or has expired. Request a new one."));
        }

        switch (await users.ChangeEmailAsync(command.UserId, newEmail, now, cancellationToken).ConfigureAwait(false))
        {
            case EmailChangeResult.AddressTaken:
                return Result.Failure<ProfileDto>(ResultError.Conflict(EmailInUseCode, "That email address belongs to another account."));
            case EmailChangeResult.NoSuchUser:
                return Result.Failure<ProfileDto>(ProfileMapping.UnknownUser);
        }

        return await ProfileMapping.LoadAsync(users, command.UserId, cancellationToken).ConfigureAwait(false);
    }
}
