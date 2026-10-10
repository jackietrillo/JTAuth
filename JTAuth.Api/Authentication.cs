using JTAuth.Application;
using JTAuth.Infrastructure.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace JTAuth.Api;

internal static class Authentication
{
    /// <summary>
    /// JTAuth checks the access tokens it receives (for the profile) the way an app does: signature against the published public
    /// key, issuer, and expiry. The audience is not fixed, because a token for any registered app is a token for the same person;
    /// instead the audience must belong to a registered, enabled app, so a disabled app's tokens stop working here too.
    /// </summary>
    public static IServiceCollection AddJTAuthAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RsaSigningKeys, JTAuthSettings>((options, keys, settings) =>
            {
                options.MapInboundClaims = false; // keep "sub" and "aud" as they are in the token
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.IssuerBase,
                    IssuerSigningKeys = keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = RequireRegisteredApp };
            });

        return services;
    }

    private static async Task RequireRegisteredApp(TokenValidatedContext context)
    {
        var audience = context.Principal?.FindFirst("aud")?.Value;
        var clients = context.HttpContext.RequestServices.GetRequiredService<IClientRepository>();
        var client = audience is null ? null : await clients.FindByAudienceAsync(audience, context.HttpContext.RequestAborted).ConfigureAwait(false);

        if (client is not { IsEnabled: true })
        {
            context.Fail("The token was not issued for a registered, enabled app.");
        }
    }
}
