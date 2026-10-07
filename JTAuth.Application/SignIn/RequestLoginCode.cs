using FluentValidation;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>Starts a sign-in: emails a one-time code to the address.</summary>
public sealed record RequestLoginCodeCommand(string ClientId, string Email) : ICommand<CodeRequestedDto>;

public sealed class RequestLoginCodeValidator : AbstractValidator<RequestLoginCodeCommand>
{
    public RequestLoginCodeValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty().MaximumLength(50);
        RuleFor(command => command.Email)
            .Must(email => EmailAddress.TryNormalize(email, out _))
            .WithMessage("Enter a valid email address.");
    }
}

/// <summary>
/// Never looks at whether the address has an account, so a known and an unknown address take the same path and get the
/// same answer. The only refusals depend on the app, the shape of the address and how many codes it was sent lately.
/// </summary>
public sealed class RequestLoginCodeHandler(
    IClientRepository clients,
    ILoginCodeRepository codes,
    IEmailSender emailSender,
    JTAuthSettings settings,
    TimeProvider clock) : ICommandHandler<RequestLoginCodeCommand, CodeRequestedDto>
{
    public const string TooManyRequestsCode = "too_many_code_requests";

    public async Task<Result<CodeRequestedDto>> HandleAsync(RequestLoginCodeCommand command, CancellationToken cancellationToken)
    {
        var (client, error) = await ClientCheck.FindUsableAsync(clients, command.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<CodeRequestedDto>(error!);
        }

        var email = EmailAddress.Normalize(command.Email);
        var now = clock.GetUtcNow();

        var lastMinute = await codes.CountIssuedSinceAsync(IdentityProvider.Email, email, now.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
        var lastHour = await codes.CountIssuedSinceAsync(IdentityProvider.Email, email, now.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (lastMinute >= settings.CodeRequestsPerAddressPerMinute || lastHour >= settings.CodeRequestsPerAddressPerHour)
        {
            return Result.Failure<CodeRequestedDto>(ResultError.TooManyRequests(TooManyRequestsCode, "Too many codes were requested for this address. Try again later."));
        }

        var code = LoginCodeRules.GenerateCode();
        var stored = LoginCode.Issue(IdentityProvider.Email, email, LoginCodeRules.Hash(settings.CodeSecret, IdentityProvider.Email, email, code), now);
        await codes.ReplaceAsync(stored, now, cancellationToken).ConfigureAwait(false);
        await emailSender.SendLoginCodeAsync(email, client.Name, code, stored.ExpiresUtc, cancellationToken).ConfigureAwait(false);

        return Result.Success(new CodeRequestedDto((int)LoginCodeRules.Lifetime.TotalSeconds));
    }
}
