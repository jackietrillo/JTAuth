// Usage: JTAuth.Database [--ensure-database] [--skip-seed] [--connection-string "<value>"]
// The connection string comes from --connection-string or the ConnectionStrings__JTAuthDb environment
// variable (what scripts/deploy-db.ps1 sets), the same key the API reads.
using JTAuth.Database;

var connectionString = ArgumentValue(args, "--connection-string")
    ?? Environment.GetEnvironmentVariable($"ConnectionStrings__{DatabaseDeployer.ConnectionStringName}");

if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync($"No connection string. Pass --connection-string or set ConnectionStrings__{DatabaseDeployer.ConnectionStringName}.").ConfigureAwait(false);
    return 2;
}

var result = DatabaseDeployer.Deploy(
    connectionString,
    ensureDatabase: args.Contains("--ensure-database"),
    seed: !args.Contains("--skip-seed"));

if (!result.Successful)
{
    await Console.Error.WriteLineAsync($"JTAuth database deploy failed: {result.Error?.Message}").ConfigureAwait(false);
    return 1;
}

Console.WriteLine(result.MigrationsApplied.Count == 0
    ? "JTAuth: schema is up to date."
    : $"JTAuth: applied {result.MigrationsApplied.Count} migration(s): {string.Join(", ", result.MigrationsApplied)}");
Console.WriteLine($"JTAuth: ran {result.SeedsRun.Count} seed script(s).");
return 0;

static string? ArgumentValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
