using System.Security.Cryptography;

namespace JTAuth.Domain;

/// <summary>Why a code can or cannot be tried right now.</summary>
public enum LoginCodeState
{
    Usable,
    Expired,
    AlreadyUsed,
    AttemptsExhausted,
}

/// <summary>
/// A one-time sign-in code as it is stored: never the code itself, only its hash. A code works once, for ten minutes,
/// for five attempts, and only while it is the newest code for that address (issuing a new one ends the older ones).
/// </summary>
public sealed record LoginCode(
    long Id,
    IdentityProvider Provider,
    string ProviderSubject,
    byte[] CodeHash,
    DateTimeOffset ExpiresUtc,
    int AttemptCount,
    DateTimeOffset? ConsumedUtc,
    DateTimeOffset CreatedUtc)
{
    /// <summary>A new code, valid for <see cref="LoginCodeRules.Lifetime"/> from <paramref name="now"/>. The database assigns the id.</summary>
    public static LoginCode Issue(IdentityProvider provider, string providerSubject, byte[] codeHash, DateTimeOffset now) =>
        new(0, provider, providerSubject, codeHash, now + LoginCodeRules.Lifetime, 0, null, now);

    /// <summary>Whether the code can still be tried. Used (or replaced) wins over expired, expired over too many attempts.</summary>
    public LoginCodeState StateAt(DateTimeOffset now)
    {
        if (ConsumedUtc is not null)
        {
            return LoginCodeState.AlreadyUsed;
        }

        if (now >= ExpiresUtc)
        {
            return LoginCodeState.Expired;
        }

        return AttemptCount >= LoginCodeRules.MaxAttempts ? LoginCodeState.AttemptsExhausted : LoginCodeState.Usable;
    }

    /// <summary>Compares in constant time, so the time taken says nothing about how many leading bytes were right.</summary>
    public bool Matches(byte[] presentedHash) => CryptographicOperations.FixedTimeEquals(CodeHash, presentedHash);
}
