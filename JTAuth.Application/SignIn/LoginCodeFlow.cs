using JTAuth.BuildingBlocks;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>
/// The two halves of proving control of an email address with a one-time code, shared by signing in and by changing the
/// sign-in email: sending a code, and redeeming it. The rules (limits, expiry, attempts, single use) live here once.
/// </summary>
internal static class LoginCodeFlow
{
    public const string TooManyRequestsCode = "too_many_code_requests";

    /// <summary>
    /// Applies the per-address limits, stores a new code (which ends the older open ones) and emails it, naming the app.
    /// Never looks at whether the address has an account.
    /// </summary>
    public static async Task<Result<CodeRequestedDto>> IssueAsync(
        ILoginCodeRepository codes, IEmailSender emailSender, JTAuthSettings settings, DateTimeOffset now, Client client, string email, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// True when <paramref name="code"/> is the newest code for the address, unused, unexpired and within its attempts, and it has
    /// now been used up. Every other case (no code, wrong digits, expired, used, replaced, out of attempts) is the same false.
    /// </summary>
    public static async Task<bool> RedeemAsync(
        ILoginCodeRepository codes, JTAuthSettings settings, DateTimeOffset now, string email, string code, CancellationToken cancellationToken)
    {
        var stored = await codes.FindLatestAsync(IdentityProvider.Email, email, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.StateAt(now) != LoginCodeState.Usable)
        {
            return false;
        }

        // The attempt is counted before the comparison, so a burst of parallel guesses is still limited to five.
        if (!await codes.TryClaimAttemptAsync(stored.Id, LoginCodeRules.MaxAttempts, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var presented = LoginCodeRules.Hash(settings.CodeSecret, IdentityProvider.Email, email, code);
        return stored.Matches(presented) && await codes.TryConsumeAsync(stored.Id, now, cancellationToken).ConfigureAwait(false);
    }
}
