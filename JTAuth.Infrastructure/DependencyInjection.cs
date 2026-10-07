using JTAuth.Application;
using JTAuth.Infrastructure.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace JTAuth.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The Dapper repositories over the JTAuth database and the token signing. The email sender is chosen by the host.</summary>
    /// <param name="signingKeyFile">Where the RSA signing key lives; created on first use.</param>
    public static IServiceCollection AddJTAuthInfrastructure(this IServiceCollection services, string connectionString, string signingKeyFile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKeyFile);

        services.AddSingleton(new JTAuthDatabase(connectionString));
        services.AddScoped<IClientRepository, ClientRepository>();
        services.AddScoped<ILoginCodeRepository, LoginCodeRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserClientRepository, UserClientRepository>();

        services.AddSingleton(_ => RsaSigningKeys.LoadOrCreate(signingKeyFile));
        services.AddSingleton<ISigningKeySource>(provider => provider.GetRequiredService<RsaSigningKeys>());
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();
        return services;
    }
}
