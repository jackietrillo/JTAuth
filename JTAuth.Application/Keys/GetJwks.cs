using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;

namespace JTAuth.Application.Keys;

/// <summary>The public keys apps validate tokens with.</summary>
public sealed record GetJwksQuery : IQuery<JwksDto>;

public sealed class GetJwksHandler(ISigningKeySource keys) : IQueryHandler<GetJwksQuery, JwksDto>
{
    public Task<Result<JwksDto>> HandleAsync(GetJwksQuery query, CancellationToken cancellationToken)
    {
        var published = keys.PublishedKeys
            .Select(key => new JsonWebKeyDto("RSA", "sig", key.Algorithm, key.KeyId, key.Modulus, key.Exponent))
            .ToList();
        return Task.FromResult(Result.Success(new JwksDto(published)));
    }
}
