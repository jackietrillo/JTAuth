using System.Security.Cryptography;
using System.Text;

namespace JTAuth.Domain;

/// <summary>How refresh tokens are made and stored.</summary>
public static class RefreshTokenRules
{
    /// <summary>Each token (and each replacement) is valid this long from the moment it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private const int TokenBytes = 32;

    /// <summary>A new opaque token (256 random bits, URL-safe) and the hash that is stored in its place.</summary>
    public static (string Token, byte[] Hash) Generate()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, Hash(token));
    }

    /// <summary>
    /// SHA-256 of the token. The token already carries 256 bits of randomness, so no secret or slow hash is needed to keep it
    /// from being guessed; the hash only means a copy of the table cannot be used to refresh anyone's session.
    /// </summary>
    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Whether a refresh token can be exchanged right now.</summary>
public enum RefreshTokenState
{
    Usable,
    Expired,
    Revoked,
}

/// <summary>
/// A refresh token as it is stored (never the token itself). Tokens form chains: a sign-in starts a chain, and each use revokes
/// the token and replaces it with the next one in the same chain. A token that is presented after it was revoked is a sign that
/// a copy leaked, so the whole chain is ended.
/// </summary>
public sealed record RefreshToken(
    long Id,
    Guid UserId,
    int ClientId,
    Guid FamilyId,
    byte[] TokenHash,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RevokedUtc,
    DateTimeOffset CreatedUtc)
{
    /// <summary>The first token of a new chain, for a person who has just signed in. The database assigns the id.</summary>
    public static RefreshToken StartChain(Guid userId, int clientId, byte[] tokenHash, DateTimeOffset now) =>
        new(0, userId, clientId, Guid.NewGuid(), tokenHash, now + RefreshTokenRules.Lifetime, null, now);

    /// <summary>The next token in this token's chain, for the same person and app, valid from <paramref name="now"/>.</summary>
    public RefreshToken Next(byte[] tokenHash, DateTimeOffset now) =>
        new(0, UserId, ClientId, FamilyId, tokenHash, now + RefreshTokenRules.Lifetime, null, now);

    /// <summary>Revoked wins over expired, so a used token that has also run out is still treated as a reuse.</summary>
    public RefreshTokenState StateAt(DateTimeOffset now)
    {
        if (RevokedUtc is not null)
        {
            return RefreshTokenState.Revoked;
        }

        return now >= ExpiresUtc ? RefreshTokenState.Expired : RefreshTokenState.Usable;
    }
}
