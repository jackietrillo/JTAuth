using FluentValidation;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>Signs in (or up) with the ID token Google gave the page after the person chose their Google account.</summary>
public sealed record SignInWithGoogleCommand(string ClientId, string IdToken) : ICommand<AuthTokensDto>;

public sealed class SignInWithGoogleValidator : AbstractValidator<SignInWithGoogleCommand>
{
    /// <summary>A Google ID token is a few kilobytes at most; anything bigger is not one, and is refused before it is parsed.</summary>
    public const int MaxTokenLength = 4096;

    public SignInWithGoogleValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty().MaximumLength(50);
        RuleFor(command => command.IdToken).NotEmpty().MaximumLength(MaxTokenLength);
    }
}

/// <summary>
/// Google proves who the person is, so no code is needed. The account is found, linked or created by <see cref="IUserRepository.GetOrCreateByGoogleAsync"/>:
/// a Google account whose verified email matches an existing email account signs in to that same account instead of making a second one.
/// Any token that is not acceptable gets the same answer, and a person's name from Google is used only for a new account.
/// </summary>
public sealed class SignInWithGoogleHandler(
    IClientRepository clients,
    IGoogleIdTokenValidator google,
    IUserRepository users,
    IUserClientRepository userClients,
    IRefreshTokenRepository refreshTokens,
    IAccessTokenIssuer tokenIssuer,
    JTAuthSettings settings,
    TimeProvider clock) : ICommandHandler<SignInWithGoogleCommand, AuthTokensDto>
{
    public const string NotConfiguredCode = "google_sign_in_unavailable";
    public const string InvalidTokenCode = "invalid_google_token";
    public const string AccountConflictCode = "google_account_conflict";

    private static readonly ResultError InvalidToken =
        ResultError.Unauthorized(InvalidTokenCode, "The Google sign-in is not valid. Try again.");

    public async Task<Result<AuthTokensDto>> HandleAsync(SignInWithGoogleCommand command, CancellationToken cancellationToken)
    {
        if (settings.UsableGoogleClientIds.Count == 0)
        {
            return Result.Failure<AuthTokensDto>(ResultError.NotFound(NotConfiguredCode, "Google sign-in is not available."));
        }

        var (client, error) = await ClientCheck.FindUsableAsync(clients, command.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<AuthTokensDto>(error!);
        }

        var profile = await google.ValidateAsync(command.IdToken, cancellationToken).ConfigureAwait(false);
        if (profile is null || !EmailAddress.TryNormalize(profile.Email, out var email))
        {
            return Result.Failure<AuthTokensDto>(InvalidToken);
        }

        var now = clock.GetUtcNow();
        var outcome = await users.GetOrCreateByGoogleAsync(profile.Subject, email, GoogleDisplayName(profile.Name), now, cancellationToken).ConfigureAwait(false);
        if (outcome.User is null)
        {
            return Result.Failure<AuthTokensDto>(ResultError.Conflict(AccountConflictCode, "This email address is already linked to a different Google account."));
        }

        await userClients.RecordSignInAsync(outcome.User.Id, client.Id, now, cancellationToken).ConfigureAwait(false);
        return Result.Success(await SessionIssuer.StartAsync(
            refreshTokens, tokenIssuer, outcome.User, client, outcome.Status == GoogleSignInStatus.Created, now, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Google's name for the person, trimmed to what a display name allows; null when it has none worth keeping.</summary>
    private static string? GoogleDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > DisplayName.MaxLength)
        {
            trimmed = trimmed[..DisplayName.MaxLength].TrimEnd();
        }

        return DisplayName.TryNormalize(trimmed, out var normalized) ? normalized : null;
    }
}
