using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Helpers;

namespace JTAuth.Database;

/// <summary>
/// Applies the numbered, forward-only migrations (journaled in dbo.SchemaVersions, each run once)
/// and then the idempotent seed scripts (not journaled, run on every deploy).
/// </summary>
public static class DatabaseDeployer
{
    public const string MigrationsPrefix = "migrations/";
    public const string SeedPrefix = "seed/";

    /// <summary>The configuration key the API and scripts use.</summary>
    public const string ConnectionStringName = "JTAuthDb";

    private static readonly Assembly ScriptAssembly = typeof(DatabaseDeployer).Assembly;

    public static DeployResult Deploy(string connectionString, bool ensureDatabase, bool seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (ensureDatabase)
        {
            EnsureDatabase.For.SqlDatabase(connectionString);
        }

        var migrations = DeployChanges.To.SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(ScriptAssembly, name => name.StartsWith(MigrationsPrefix, StringComparison.Ordinal))
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        var migrationResult = migrations.PerformUpgrade();
        if (!migrationResult.Successful || !seed)
        {
            return DeployResult.From(migrationResult, seeds: null);
        }

        var seeds = DeployChanges.To.SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(ScriptAssembly, name => name.StartsWith(SeedPrefix, StringComparison.Ordinal))
            .JournalTo(new NullJournal())
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        return DeployResult.From(migrationResult, seeds.PerformUpgrade());
    }
}

public sealed record DeployResult(bool Successful, IReadOnlyList<string> MigrationsApplied, IReadOnlyList<string> SeedsRun, Exception? Error)
{
    internal static DeployResult From(DatabaseUpgradeResult migrations, DatabaseUpgradeResult? seeds) =>
        new(
            migrations.Successful && (seeds?.Successful ?? true),
            migrations.Scripts.Select(script => script.Name).ToList(),
            seeds?.Scripts.Select(script => script.Name).ToList() ?? [],
            migrations.Error ?? seeds?.Error);
}
