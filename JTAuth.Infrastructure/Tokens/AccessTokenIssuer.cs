using JTAuth.Application;
using JTAuth.Domain;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JTAuth.Infrastructure.Tokens;

/// <summary>
/// Signs the short-lived access token an app validates locally: <c>iss</c> (JTAuth), <c>aud</c> (the client's audience),
/// <c>sub</c> (the person), <c>name</c> (only when set), <c>iat</c>, <c>exp</c> and a unique <c>jti</c>.
/// </summary>
public sealed class AccessTokenIssuer(RsaSigningKeys keys, JTAuthSettings settings) : IAccessTokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedAccessToken Issue(User user, Client client, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(client);

        var expires = now + TokenRules.AccessTokenLifetime;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id.ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims["name"] = user.DisplayName;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.IssuerBase,
            Audience = client.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keys.Credentials,
        };

        return new IssuedAccessToken(Handler.CreateToken(descriptor), expires);
    }
}
