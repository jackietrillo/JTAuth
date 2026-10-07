using Dapper;
using JTAuth.Application;
using JTAuth.Domain;
using Microsoft.Data.SqlClient;

namespace JTAuth.Infrastructure;

public sealed class LoginCodeRepository(JTAuthDatabase database) : ILoginCodeRepository
{
    public async Task<int> CountIssuedSinceAsync(IdentityProvider provider, string providerSubject, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.LoginCode WHERE Provider = @provider AND ProviderSubject = @providerSubject AND CreatedUtc > @since;",
            new { provider = provider.ToString(), providerSubject, since = SqlTime.ToSql(since) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task ReplaceAsync(LoginCode newCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newCode);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // An older code that is still open is ended the same way a used one is: it can no longer be redeemed.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.LoginCode SET ConsumedUtc = @now
            WHERE Provider = @provider AND ProviderSubject = @providerSubject AND ConsumedUtc IS NULL;

            INSERT dbo.LoginCode (Provider, ProviderSubject, CodeHash, ExpiresUtc, AttemptCount, CreatedUtc)
            VALUES (@provider, @providerSubject, @codeHash, @expiresUtc, 0, @createdUtc);
            """,
            new
            {
                now = SqlTime.ToSql(now),
                provider = newCode.Provider.ToString(),
                providerSubject = newCode.ProviderSubject,
                codeHash = newCode.CodeHash,
                expiresUtc = SqlTime.ToSql(newCode.ExpiresUtc),
                createdUtc = SqlTime.ToSql(newCode.CreatedUtc),
            },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginCode?> FindLatestAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<LoginCodeRow>(new CommandDefinition(
            """
            SELECT TOP (1) Id, CodeHash, ExpiresUtc, AttemptCount, ConsumedUtc, CreatedUtc
            FROM dbo.LoginCode
            WHERE Provider = @provider AND ProviderSubject = @providerSubject
            ORDER BY Id DESC;
            """,
            new { provider = provider.ToString(), providerSubject }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new LoginCode(row.Id, provider, providerSubject, row.CodeHash, SqlTime.FromSql(row.ExpiresUtc), row.AttemptCount, SqlTime.FromSql(row.ConsumedUtc), SqlTime.FromSql(row.CreatedUtc));
    }

    public async Task<bool> TryClaimAttemptAsync(long codeId, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.LoginCode SET AttemptCount = AttemptCount + 1 WHERE Id = @codeId AND ConsumedUtc IS NULL AND AttemptCount < @maxAttempts;",
            new { codeId, maxAttempts }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<bool> TryConsumeAsync(long codeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.LoginCode SET ConsumedUtc = @now WHERE Id = @codeId AND ConsumedUtc IS NULL AND ExpiresUtc > @now;",
            new { codeId, now = SqlTime.ToSql(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    private sealed record LoginCodeRow(long Id, byte[] CodeHash, DateTime ExpiresUtc, int AttemptCount, DateTime? ConsumedUtc, DateTime CreatedUtc);
}
