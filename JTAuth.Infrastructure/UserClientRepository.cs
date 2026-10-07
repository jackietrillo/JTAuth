using Dapper;
using JTAuth.Application;

namespace JTAuth.Infrastructure;

public sealed class UserClientRepository(JTAuthDatabase database) : IUserClientRepository
{
    public async Task RecordSignInAsync(Guid userId, int clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // First sign-in inserts; later ones only move LastSignInUtc (never backwards, so the row's
        // last >= first check holds even if two servers' clocks differ). ContactConsentUtc and RevokedUtc are not mentioned.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.UserClient WITH (HOLDLOCK) AS target
            USING (VALUES (@userId, @clientId)) AS source (UserId, ClientId)
                ON target.UserId = source.UserId AND target.ClientId = source.ClientId
            WHEN MATCHED THEN
                UPDATE SET LastSignInUtc = CASE WHEN @now > target.LastSignInUtc THEN @now ELSE target.LastSignInUtc END
            WHEN NOT MATCHED THEN
                INSERT (UserId, ClientId, FirstSignInUtc, LastSignInUtc) VALUES (@userId, @clientId, @now, @now);
            """,
            new { userId, clientId, now = SqlTime.ToSql(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
