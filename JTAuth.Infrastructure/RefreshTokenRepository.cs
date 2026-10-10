using Dapper;
using JTAuth.Application;
using JTAuth.Domain;
using Microsoft.Data.SqlClient;

namespace JTAuth.Infrastructure;

public sealed class RefreshTokenRepository(JTAuthDatabase database) : IRefreshTokenRepository
{
    private const string InsertSql = """
        INSERT dbo.RefreshToken (UserId, ClientId, FamilyId, TokenHash, ExpiresUtc, CreatedUtc)
        OUTPUT inserted.Id
        VALUES (@userId, @clientId, @familyId, @tokenHash, @expiresUtc, @createdUtc);
        """;

    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(InsertSql, Parameters(token), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
            "SELECT Id, UserId, ClientId, FamilyId, ExpiresUtc, RevokedUtc, CreatedUtc FROM dbo.RefreshToken WHERE TokenHash = @tokenHash;",
            new { tokenHash }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new RefreshToken(row.Id, row.UserId, row.ClientId, row.FamilyId, tokenHash, SqlTime.FromSql(row.ExpiresUtc), SqlTime.FromSql(row.RevokedUtc), SqlTime.FromSql(row.CreatedUtc));
    }

    public async Task<bool> RotateAsync(long tokenId, RefreshToken replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The conditional UPDATE is what makes a token single use: of any number of callers presenting it at once, one changes the row.
        var revoked = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.RefreshToken SET RevokedUtc = @now WHERE Id = @tokenId AND RevokedUtc IS NULL AND ExpiresUtc > @now;",
            new { tokenId, now = SqlTime.ToSql(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (revoked != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var replacementId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(InsertSql, Parameters(replacement), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.RefreshToken SET ReplacedByTokenId = @replacementId WHERE Id = @tokenId;",
            new { tokenId, replacementId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task RevokeChainAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.RefreshToken SET RevokedUtc = @now WHERE FamilyId = @familyId AND RevokedUtc IS NULL;",
            new { familyId, now = SqlTime.ToSql(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object Parameters(RefreshToken token) => new
    {
        userId = token.UserId,
        clientId = token.ClientId,
        familyId = token.FamilyId,
        tokenHash = token.TokenHash,
        expiresUtc = SqlTime.ToSql(token.ExpiresUtc),
        createdUtc = SqlTime.ToSql(token.CreatedUtc),
    };

    private sealed record RefreshTokenRow(long Id, Guid UserId, int ClientId, Guid FamilyId, DateTime ExpiresUtc, DateTime? RevokedUtc, DateTime CreatedUtc);
}
