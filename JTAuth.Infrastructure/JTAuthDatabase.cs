using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace JTAuth.Infrastructure;

/// <summary>Opens connections to the JTAuth database (ConnectionStrings:JTAuthDb).</summary>
public sealed class JTAuthDatabase(string connectionString)
{
    static JTAuthDatabase()
    {
        // Send timestamps as datetime2 (millisecond precision, like the columns), not datetime, which rounds to 1/300 s.
        SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Timestamps are UTC <c>datetime2</c> in the database and <see cref="DateTimeOffset"/> in the code.</summary>
internal static class SqlTime
{
    public static DateTime ToSql(DateTimeOffset value) => value.UtcDateTime;

    public static DateTimeOffset FromSql(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? FromSql(DateTime? value) => value is null ? null : FromSql(value.Value);
}
