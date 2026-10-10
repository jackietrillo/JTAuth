using JTAuth.BuildingBlocks;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Profile;

internal static class ProfileMapping
{
    public const string UnknownUserCode = "unknown_user";

    public const string StatusLinked = "Linked";
    public const string StatusNotLinked = "NotLinked";
    public const string StatusComingSoon = "ComingSoon";

    /// <summary>The token is genuine but its person no longer exists, so the caller must sign in again.</summary>
    public static ResultError UnknownUser { get; } = ResultError.Unauthorized(UnknownUserCode, "This account no longer exists. Sign in again.");

    /// <summary>The display name and how the person can sign in: email and Google as linked or not, and phone as coming soon.</summary>
    public static ProfileDto ToDto(User user, IReadOnlyList<UserIdentity> identities)
    {
        var email = identities.FirstOrDefault(identity => identity.Provider == IdentityProvider.Email);
        var google = identities.Any(identity => identity.Provider == IdentityProvider.Google);

        return new ProfileDto(
            user.DisplayName,
            [
                new SignInMethodDto(nameof(IdentityProvider.Email), email is null ? StatusNotLinked : StatusLinked, email?.ProviderSubject),
                new SignInMethodDto(nameof(IdentityProvider.Google), google ? StatusLinked : StatusNotLinked, null),
                new SignInMethodDto(nameof(IdentityProvider.Phone), StatusComingSoon, null),
            ]);
    }

    public static async Task<Result<ProfileDto>> LoadAsync(IUserRepository users, Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<ProfileDto>(UnknownUser);
        }

        var identities = await users.GetIdentitiesAsync(userId, cancellationToken).ConfigureAwait(false);
        return Result.Success(ToDto(user, identities));
    }
}
