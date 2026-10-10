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
    public const string TooManyRequestsCode = LoginCodeFlow.TooManyRequestsCode;

    public async Task<Result<CodeRequestedDto>> HandleAsync(RequestLoginCodeCommand command, CancellationToken cancellationToken)
    {
        var (client, error) = await ClientCheck.FindUsableAsync(clients, command.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<CodeRequestedDto>(error!);
        }

        return await LoginCodeFlow.IssueAsync(codes, emailSender, settings, clock.GetUtcNow(), client, EmailAddress.Normalize(command.Email), cancellationToken).ConfigureAwait(false);
    }
}
