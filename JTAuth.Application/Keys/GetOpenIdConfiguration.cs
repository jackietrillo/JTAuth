using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;

namespace JTAuth.Application.Keys;

/// <summary>The discovery document: the issuer and where its keys are published.</summary>
public sealed record GetOpenIdConfigurationQuery : IQuery<OpenIdConfigurationDto>;

public sealed class GetOpenIdConfigurationHandler(JTAuthSettings settings, ISigningKeySource keys)
    : IQueryHandler<GetOpenIdConfigurationQuery, OpenIdConfigurationDto>
{
    public const string JwksPath = "/.well-known/jwks.json";

    public Task<Result<OpenIdConfigurationDto>> HandleAsync(GetOpenIdConfigurationQuery query, CancellationToken cancellationToken)
    {
        var algorithms = keys.PublishedKeys.Select(key => key.Algorithm).Distinct(StringComparer.Ordinal).ToList();
        return Task.FromResult(Result.Success(new OpenIdConfigurationDto(settings.IssuerBase, settings.IssuerBase + JwksPath, algorithms)));
    }
}
