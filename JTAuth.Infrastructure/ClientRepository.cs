using Dapper;
using JTAuth.Application;
using JTAuth.Domain;

namespace JTAuth.Infrastructure;

public sealed class ClientRepository(JTAuthDatabase database) : IClientRepository
{
    public async Task<Client?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Client>(new CommandDefinition(
            "SELECT Id, ClientId, Name, Audience, IsEnabled FROM dbo.Client WHERE ClientId = @clientId;",
            new { clientId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<Client?> FindByAudienceAsync(string audience, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<Client>(new CommandDefinition(
            "SELECT TOP (1) Id, ClientId, Name, Audience, IsEnabled FROM dbo.Client WHERE Audience = @audience ORDER BY Id;",
            new { audience }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
