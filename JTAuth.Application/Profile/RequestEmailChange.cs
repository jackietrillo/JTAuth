using FluentValidation;
using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Profile;

/// <summary>
/// Starts changing the sign-in email: a code is sent to the new address. <paramref name="Audience"/> is the <c>aud</c> of the
/// caller's access token, which names the app the email is sent for.
/// </summary>
public sealed record RequestEmailChangeCommand(Guid UserId, string Audience, string NewEmail) : ICommand<CodeRequestedDto>;

public sealed class RequestEmailChangeValidator : AbstractValidator<RequestEmailChangeCommand>
{
    public RequestEmailChangeValidator()
    {
        RuleFor(command => command.NewEmail)
            .Must(email => EmailAddress.TryNormalize(email, out _))
            .WithMessage("Enter a valid email address.");
    }
}

/// <summary>
/// The answer is the same whether the new address is free or already belongs to another account: in the second case no code is
/// sent, so a signed-in person cannot use this to find out which addresses have accounts.
/// </summary>
public sealed class RequestEmailChangeHandler(
    IClientRepository clients,
    IUserRepository users,
    ILoginCodeRepository codes,
    IEmailSender emailSender,
    JTAuthSettings settings,
    TimeProvider clock) : ICommandHandler<RequestEmailChangeCommand, CodeRequestedDto>
{
    public const string AlreadyYoursCode = "email_unchanged";

    public async Task<Result<CodeRequestedDto>> HandleAsync(RequestEmailChangeCommand command, CancellationToken cancellationToken)
    {
        var client = await clients.FindByAudienceAsync(command.Audience, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<CodeRequestedDto>(ResultError.Unauthorized(ClientCheck.UnknownClientCode, "The token was not issued for a registered app."));
        }

        if (!client.IsEnabled)
        {
            return Result.Failure<CodeRequestedDto>(ResultError.Forbidden(ClientCheck.DisabledClientCode, "The client is disabled."));
        }

        if (await users.FindByIdAsync(command.UserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<CodeRequestedDto>(ProfileMapping.UnknownUser);
        }

        var newEmail = EmailAddress.Normalize(command.NewEmail);
        var owner = await users.FindOwnerAsync(IdentityProvider.Email, newEmail, cancellationToken).ConfigureAwait(false);
        if (owner == command.UserId)
        {
            return Result.Failure<CodeRequestedDto>(ResultError.Validation(AlreadyYoursCode, "That is already your sign-in email address."));
        }

        if (owner is not null)
        {
            return Result.Success(new CodeRequestedDto((int)LoginCodeRules.Lifetime.TotalSeconds));
        }

        return await LoginCodeFlow.IssueAsync(codes, emailSender, settings, clock.GetUtcNow(), client, newEmail, cancellationToken).ConfigureAwait(false);
    }
}
