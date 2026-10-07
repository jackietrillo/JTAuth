using Dapper;
using JTAuth.Database;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace JTAuth.IntegrationTests;

/// <summary>
/// A throwaway JTAuth database with the real migrations and seed applied, dropped afterwards.
/// <list type="bullet">
/// <item>With the <c>JTAUTH_TEST_SQLSERVER</c> environment variable set (e.g. <c>localhost\SQLEXPRESS</c>),
/// it is created on that server with Windows sign-in.</item>
/// <item>Otherwise a SQL Server container is started with Testcontainers (needs Docker).</item>
/// <item>If neither is available, the tests are skipped, not failed.</item>
/// </list>
/// </summary>
public sealed class SqlServerDatabase : IAsyncLifetime
{
    public const string ServerVariable = "JTAUTH_TEST_SQLSERVER";

    private MsSqlContainer? container;
    private string? masterConnectionString;
    private string? databaseName;

    public string ConnectionString { get; private set; } = string.Empty;

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        databaseName = $"JTAuthTest_{Guid.NewGuid():N}";
        var server = Environment.GetEnvironmentVariable(ServerVariable);

        if (!string.IsNullOrWhiteSpace(server))
        {
            masterConnectionString = $"Server={server};Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
        }
        else
        {
            try
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await container.StartAsync().ConfigureAwait(false);
                masterConnectionString = container.GetConnectionString();
            }
#pragma warning disable CA1031 // Any failure to start Docker means "no SQL Server here": skip, do not fail.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                SkipReason = $"No SQL Server for integration tests: set {ServerVariable} (e.g. localhost\\SQLEXPRESS) or start Docker. ({exception.GetType().Name})";
                return;
            }
        }

        ConnectionString = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = databaseName }.ConnectionString;
        var result = DatabaseDeployer.Deploy(ConnectionString, ensureDatabase: true, seed: true);
        if (!result.Successful)
        {
            throw new InvalidOperationException("Deploying the test database failed.", result.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (masterConnectionString is null || SkipReason is not null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The name is generated above, not user input.
        command.CommandText = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void SkipIfUnavailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    /// <summary>Runs a statement directly, for arranging and checking rows.</summary>
    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteAsync(sql, parameters).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.QueryAsync<T>(sql, parameters).ConfigureAwait(false)).AsList();
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters).ConfigureAwait(false))!;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerTestGroup : ICollectionFixture<SqlServerDatabase>
{
    public const string Name = "SQL Server";
}
