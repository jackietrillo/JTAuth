using JTAuth.Domain;

namespace JTAuth.Application;

// What the Application layer needs from storage, tokens and email. Infrastructure implements these.

public interface IClientRepository
{
    /// <summary>The registered app with this client id, or null.</summary>
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>The registered app whose tokens carry this audience, or null. Used to tell which app an access token was issued for.</summary>
    Task<Client?> FindByAudienceAsync(string audience, CancellationToken cancellationToken);
}

public interface ILoginCodeRepository
{
    /// <summary>How many codes were issued to this address since the given moment (for rate limiting).</summary>
    Task<int> CountIssuedSinceAsync(IdentityProvider provider, string providerSubject, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Ends every code still open for the address and stores the new one, as one change.</summary>
    Task ReplaceAsync(LoginCode newCode, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The newest code for the address, used or not, or null. Older codes are never eligible.</summary>
    Task<LoginCode?> FindLatestAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken);

    /// <summary>
    /// Counts one attempt against the code, but only while it is unused and below <paramref name="maxAttempts"/>.
    /// Returns false when no attempt was left. Done before the code is compared, so parallel guesses cannot exceed the limit.
    /// </summary>
    Task<bool> TryClaimAttemptAsync(long codeId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Marks the code used, but only if it was unused and has not expired. Exactly one of any number of callers gets true.</summary>
    Task<bool> TryConsumeAsync(long codeId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IUserRepository
{
    /// <summary>
    /// The person who owns this sign-in identity, creating the person (and the identity) when nobody does.
    /// Safe under concurrent calls for the same identity: exactly one caller creates, the others see that person.
    /// </summary>
    Task<(User User, bool IsNew)> GetOrCreateByIdentityAsync(IdentityProvider provider, string providerSubject, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The person with this id, or null.</summary>
    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The id of the person who owns this sign-in identity, or null when nobody does.</summary>
    Task<Guid?> FindOwnerAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken);

    /// <summary>The ways this person can sign in (at most one per provider).</summary>
    Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Sets the display name. Returns false when there is no such person.</summary>
    Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="newEmail"/> the person's sign-in email (replacing the old address, or adding one when they had none).
    /// Refused when the address already belongs to someone else.
    /// </summary>
    Task<EmailChangeResult> ChangeEmailAsync(Guid userId, string newEmail, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A sign-in identity as the person sees it: how they sign in, and the address or id behind it.</summary>
public sealed record UserIdentity(IdentityProvider Provider, string ProviderSubject);

public enum EmailChangeResult
{
    Changed,
    AddressTaken,
    NoSuchUser,
}

public interface IRefreshTokenRepository
{
    /// <summary>Stores the first token of a new chain.</summary>
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);

    /// <summary>The stored token with this hash, used or not, or null.</summary>
    Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the token and stores its replacement in the same chain, as one change, but only if the token is still unused and
    /// unexpired. Returns false when it was not (someone else used it first). Exactly one of any number of callers gets true.
    /// </summary>
    Task<bool> RotateAsync(long tokenId, RefreshToken replacement, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes every token of the chain that is not revoked yet.</summary>
    Task RevokeChainAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IUserClientRepository
{
    /// <summary>
    /// Records a successful sign-in to an app: the first one creates the row and sets the first sign-in time, later ones move the
    /// last sign-in time forward. Contact consent and revocation are never touched.
    /// </summary>
    Task RecordSignInAsync(Guid userId, int clientId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IEmailSender
{
    /// <summary>Emails a sign-in code, naming the app that asked for it ("Your CityBars code is 123456").</summary>
    Task SendLoginCodeAsync(string toAddress, string appName, string code, DateTimeOffset expiresUtc, CancellationToken cancellationToken);
}

/// <summary>A signed access token and when it stops being valid.</summary>
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresUtc);

public interface IAccessTokenIssuer
{
    /// <summary>A token for this person, addressed to the app's audience.</summary>
    IssuedAccessToken Issue(User user, Client client, DateTimeOffset now);
}

/// <summary>The public half of a signing key, as published for apps to validate tokens with.</summary>
public sealed record PublicSigningKey(string KeyId, string Algorithm, string Modulus, string Exponent);

public interface ISigningKeySource
{
    /// <summary>Every key whose tokens may still be in circulation, the current one included.</summary>
    IReadOnlyList<PublicSigningKey> PublishedKeys { get; }
}
