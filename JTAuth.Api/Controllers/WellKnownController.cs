using JTAuth.Application.Keys;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace JTAuth.Api.Controllers;

/// <summary>What apps read to validate tokens with stock JWT middleware. Anonymous, and cacheable so JTAuth is not asked per request.</summary>
[Route(".well-known")]
[ResponseCache(Duration = CacheSeconds, Location = ResponseCacheLocation.Any)]
public sealed class WellKnownController(
    IQueryHandler<GetJwksQuery, JwksDto> jwks,
    IQueryHandler<GetOpenIdConfigurationQuery, OpenIdConfigurationDto> configuration) : ApiController
{
    public const int CacheSeconds = 3600;

    [HttpGet("jwks.json")]
    public async Task<ActionResult<JwksDto>> GetJwks(CancellationToken cancellationToken) =>
        Respond(await jwks.HandleAsync(new GetJwksQuery(), cancellationToken));

    [HttpGet("openid-configuration")]
    public async Task<ActionResult<OpenIdConfigurationDto>> GetOpenIdConfiguration(CancellationToken cancellationToken) =>
        Respond(await configuration.HandleAsync(new GetOpenIdConfigurationQuery(), cancellationToken));
}
