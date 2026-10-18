using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Profile;

/// <summary>
/// The person deletes their account from one app. It needs a sign-in within the last few minutes
/// (<see cref="AccountRules.RecentSignInWindow"/>), so a session left open on a shared computer cannot do it. If another app also has
/// the person, only their place in this app is removed; otherwise the whole account goes.
/// </summary>
public sealed record DeleteAccountCommand(Guid UserId, string Audience, DateTimeOffset? SignedInUtc) : ICommand<AccountDeletedDto>;

public sealed class DeleteAccountHandler(IClientRepository clients, IUserRepository users, TimeProvider clock) : ICommandHandler<DeleteAccountCommand, AccountDeletedDto>
{
    public const string RecentSignInRequiredCode = "recent_sign_in_required";

    public async Task<Result<AccountDeletedDto>> HandleAsync(DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        if (!AccountRules.IsRecentSignIn(command.SignedInUtc, clock.GetUtcNow()))
        {
            return Result.Failure<AccountDeletedDto>(ResultError.Forbidden(RecentSignInRequiredCode, "Sign in again to confirm, then delete your account."));
        }

        var client = await clients.FindByAudienceAsync(command.Audience, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<AccountDeletedDto>(ResultError.Unauthorized("unknown_client", "The token was not issued for a registered app."));
        }

        return await users.DeleteAccountAsync(command.UserId, client.Id, cancellationToken).ConfigureAwait(false) switch
        {
            AccountDeletion.AccountRemoved => Result.Success(new AccountDeletedDto(AccountRemoved: true)),
            AccountDeletion.LeftApp => Result.Success(new AccountDeletedDto(AccountRemoved: false)),
            _ => Result.Failure<AccountDeletedDto>(ProfileMapping.UnknownUser),
        };
    }
}
