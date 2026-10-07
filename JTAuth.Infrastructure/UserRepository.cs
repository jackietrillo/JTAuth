using Dapper;
using JTAuth.Application;
using JTAuth.Domain;
using Microsoft.Data.SqlClient;

namespace JTAuth.Infrastructure;

public sealed class UserRepository(JTAuthDatabase database) : IUserRepository
{
    // SQL Server error numbers for a duplicate key: unique constraint (2627) and unique index (2601).
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public async Task<(User User, bool IsNew)> GetOrCreateByIdentityAsync(
        IdentityProvider provider, string providerSubject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await FindAsync(connection, provider, providerSubject, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return (existing, false);
        }

        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var id = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                DECLARE @created TABLE (Id uniqueidentifier);
                INSERT dbo.[User] (CreatedUtc) OUTPUT inserted.Id INTO @created VALUES (@now);
                INSERT dbo.UserIdentity (UserId, Provider, ProviderSubject, VerifiedUtc)
                    SELECT Id, @provider, @providerSubject, @now FROM @created;
                SELECT Id FROM @created;
                """,
                new { now = SqlTime.ToSql(now), provider = provider.ToString(), providerSubject },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return (new User(id, null, now), true);
        }
        catch (SqlException exception) when (exception.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            // Another sign-in for the same identity created the person first: that person is the answer.
            var winner = await FindAsync(connection, provider, providerSubject, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A duplicate sign-in identity was reported but no person owns it.", exception);
            return (winner, false);
        }
    }

    private static async Task<User?> FindAsync(SqlConnection connection, IdentityProvider provider, string providerSubject, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT u.Id, u.DisplayName, u.CreatedUtc
            FROM dbo.UserIdentity i
            JOIN dbo.[User] u ON u.Id = i.UserId
            WHERE i.Provider = @provider AND i.ProviderSubject = @providerSubject;
            """,
            new { provider = provider.ToString(), providerSubject }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : new User(row.Id, row.DisplayName, SqlTime.FromSql(row.CreatedUtc));
    }

    private sealed record UserRow(Guid Id, string? DisplayName, DateTime CreatedUtc);
}
