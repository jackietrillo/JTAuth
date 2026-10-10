using System.Globalization;
using JTAuth.Application;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace JTAuth.Infrastructure.Google;

/// <summary>
/// Checks the ID token Google gave a page after the person chose their Google account: Google's signature (against the keys Google
/// publishes, fetched and cached by the configuration manager), Google as the issuer, one of this service's Google client ids as the
/// audience, and the expiry. Only a token whose email address Google has verified is accepted, because linking to an existing account
/// by email is only safe for an address the person has proved they control.
/// </summary>
public sealed class GoogleIdTokenValidator(JTAuthSettings settings, BaseConfigurationManager googleConfiguration) : IGoogleIdTokenValidator
{
    /// <summary>Where Google publishes its issuer and signing keys.</summary>
    public const string DiscoveryAddress = "https://accounts.google.com/.well-known/openid-configuration";

    private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The manager production uses: Google's discovery document over HTTPS, cached, refreshed when a key is not found.</summary>
    public static BaseConfigurationManager CreateGoogleConfigurationManager() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(DiscoveryAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });

    public async Task<GoogleProfile?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        var clientIds = settings.UsableGoogleClientIds;
        if (clientIds.Count == 0 || string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuers = Issuers,
            ValidAudiences = clientIds,
            ConfigurationManager = googleConfiguration,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        var result = await Handler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsValid)
        {
            return null;
        }

        var claims = result.ClaimsIdentity;
        var subject = claims.FindFirst("sub")?.Value;
        var email = claims.FindFirst("email")?.Value;
        var emailVerified = bool.TryParse(claims.FindFirst("email_verified")?.Value, out var verified) && verified;

        return string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) || !emailVerified
            ? null
            : new GoogleProfile(subject, email, claims.FindFirst("name")?.Value);
    }
}
