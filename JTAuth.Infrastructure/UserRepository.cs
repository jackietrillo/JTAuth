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

    public async Task<GoogleSignInOutcome> GetOrCreateByGoogleAsync(
        string googleSubject, string email, string? displayName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Two sign-ins can race to create the same person (the same Google account, or the same email address from a code sign-in).
        // The unique keys make the loser fail; trying again then finds the person the winner made.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await TryGetOrCreateByGoogleAsync(connection, googleSubject, email, displayName, now, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException exception) when (exception.Number is UniqueConstraintViolation or UniqueIndexViolation)
            {
                // Look again from the start.
            }
        }

        throw new InvalidOperationException("The Google account could not be signed in because of repeated conflicting changes.");
    }

    private static async Task<GoogleSignInOutcome> TryGetOrCreateByGoogleAsync(
        SqlConnection connection, string googleSubject, string email, string? displayName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await FindAsync(connection, IdentityProvider.Google, googleSubject, cancellationToken, transaction).ConfigureAwait(false) is { } known)
        {
            return new GoogleSignInOutcome(known, GoogleSignInStatus.SignedIn);
        }

        var ownerId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT UserId FROM dbo.UserIdentity WHERE Provider = 'Email' AND ProviderSubject = @email;",
            new { email }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (ownerId is { } owner)
        {
            var hasGoogle = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.UserIdentity WHERE UserId = @owner AND Provider = 'Google') THEN 1 ELSE 0 END AS bit);",
                new { owner }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (hasGoogle)
            {
                return new GoogleSignInOutcome(null, GoogleSignInStatus.Conflict);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.UserIdentity (UserId, Provider, ProviderSubject, VerifiedUtc) VALUES (@owner, 'Google', @googleSubject, @now);",
                new { owner, googleSubject, now = SqlTime.ToSql(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var linked = await connection.QuerySingleAsync<UserRow>(new CommandDefinition(
                "SELECT Id, DisplayName, CreatedUtc FROM dbo.[User] WHERE Id = @owner;",
                new { owner }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GoogleSignInOutcome(new User(linked.Id, linked.DisplayName, SqlTime.FromSql(linked.CreatedUtc)), GoogleSignInStatus.Linked);
        }

        // A new person gets both identities: the Google account, and the email address Google has verified, so a later sign-in with
        // a code for the same address reaches this account and no second account can be made for it.
        var id = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            DECLARE @created TABLE (Id uniqueidentifier);
            INSERT dbo.[User] (DisplayName, CreatedUtc) OUTPUT inserted.Id INTO @created VALUES (@displayName, @now);
            INSERT dbo.UserIdentity (UserId, Provider, ProviderSubject, VerifiedUtc)
                SELECT Id, 'Google', @googleSubject, @now FROM @created;
            INSERT dbo.UserIdentity (UserId, Provider, ProviderSubject, VerifiedUtc)
                SELECT Id, 'Email', @email, @now FROM @created;
            SELECT Id FROM @created;
            """,
            new { displayName, googleSubject, email, now = SqlTime.ToSql(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GoogleSignInOutcome(new User(id, displayName, now), GoogleSignInStatus.Created);
    }

    public async Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT Id, DisplayName, CreatedUtc FROM dbo.[User] WHERE Id = @userId;",
            new { userId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : new User(row.Id, row.DisplayName, SqlTime.FromSql(row.CreatedUtc));
    }

    public async Task<Guid?> FindOwnerAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT UserId FROM dbo.UserIdentity WHERE Provider = @provider AND ProviderSubject = @providerSubject;",
            new { provider = provider.ToString(), providerSubject }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Provider, string ProviderSubject)>(new CommandDefinition(
            "SELECT Provider, ProviderSubject FROM dbo.UserIdentity WHERE UserId = @userId ORDER BY Id;",
            new { userId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new UserIdentity(Enum.Parse<IdentityProvider>(row.Provider), row.ProviderSubject)).ToList();
    }

    public async Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.[User] SET DisplayName = @displayName WHERE Id = @userId;",
            new { userId, displayName }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<EmailChangeResult> ChangeEmailAsync(Guid userId, string newEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.[User] WHERE Id = @userId) THEN 1 ELSE 0 END AS bit);",
                new { userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (!exists)
            {
                return EmailChangeResult.NoSuchUser;
            }

            // The person's email identity moves to the new address; a person with no email identity (a Google-only account) gains one.
            // The unique key on (Provider, ProviderSubject) is what refuses an address another account already owns.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.UserIdentity SET ProviderSubject = @newEmail, VerifiedUtc = @now
                WHERE UserId = @userId AND Provider = 'Email';

                IF @@ROWCOUNT = 0
                    INSERT dbo.UserIdentity (UserId, Provider, ProviderSubject, VerifiedUtc) VALUES (@userId, 'Email', @newEmail, @now);
                """,
                new { userId, newEmail, now = SqlTime.ToSql(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return EmailChangeResult.Changed;
        }
        catch (SqlException exception) when (exception.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            return EmailChangeResult.AddressTaken;
        }
    }

    public async Task<AccountDeletion> DeleteAccountAsync(Guid userId, int clientId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The person is locked for the whole change, so a sign-in to another app cannot slip in between "no other app has them" and the delete.
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.[User] WITH (UPDLOCK, HOLDLOCK) WHERE Id = @userId) THEN 1 ELSE 0 END AS bit);",
            new { userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AccountDeletion.NoSuchUser;
        }

        var usesAnotherApp = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.UserClient WHERE UserId = @userId AND ClientId <> @clientId)
                          OR EXISTS (SELECT 1 FROM dbo.RefreshToken WHERE UserId = @userId AND ClientId <> @clientId) THEN 1 ELSE 0 END AS bit);
            """,
            new { userId, clientId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // A refresh token points at the one that replaced it, so those links are cut before the rows go.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.RefreshToken SET ReplacedByTokenId = NULL WHERE UserId = @userId AND (@everything = 1 OR ClientId = @clientId);
            DELETE dbo.RefreshToken WHERE UserId = @userId AND (@everything = 1 OR ClientId = @clientId);
            DELETE dbo.UserClient WHERE UserId = @userId AND (@everything = 1 OR ClientId = @clientId);
            """,
            new { userId, clientId, everything = usesAnotherApp ? 0 : 1 }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (usesAnotherApp)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AccountDeletion.LeftApp;
        }

        // Nothing is left that says who they are: the codes sent to their addresses, their identities, then the person.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE lc FROM dbo.LoginCode lc
            JOIN dbo.UserIdentity ui ON ui.Provider = lc.Provider AND ui.ProviderSubject = lc.ProviderSubject
            WHERE ui.UserId = @userId;
            DELETE dbo.UserIdentity WHERE UserId = @userId;
            DELETE dbo.[User] WHERE Id = @userId;
            """,
            new { userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AccountDeletion.AccountRemoved;
    }

    private static async Task<User?> FindAsync(
        SqlConnection connection, IdentityProvider provider, string providerSubject, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT u.Id, u.DisplayName, u.CreatedUtc
            FROM dbo.UserIdentity i
            JOIN dbo.[User] u ON u.Id = i.UserId
            WHERE i.Provider = @provider AND i.ProviderSubject = @providerSubject;
            """,
            new { provider = provider.ToString(), providerSubject }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : new User(row.Id, row.DisplayName, SqlTime.FromSql(row.CreatedUtc));
    }

    private sealed record UserRow(Guid Id, string? DisplayName, DateTime CreatedUtc);
}
