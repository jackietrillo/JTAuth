using Dapper;
using JTAuth.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JTAuth.Api;

/// <summary>Ready only when the JTAuth database answers.</summary>
public sealed class DatabaseHealthCheck(JTAuthDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // Any failure to reach the database means "not ready".
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("The JTAuth database is not reachable.", exception);
        }
    }
}
