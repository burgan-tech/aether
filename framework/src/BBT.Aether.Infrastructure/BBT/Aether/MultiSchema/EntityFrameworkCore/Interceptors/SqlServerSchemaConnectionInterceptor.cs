using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Aether.MultiSchema.EntityFrameworkCore.Interceptors;

/// <summary>
/// Connection interceptor for SQL Server that sets the default schema
/// when a connection is opened.
/// Note: SQL Server uses schemas within databases, so this sets the session default schema.
/// Requires Microsoft.Data.SqlClient package to be installed.
/// </summary>
public sealed class SqlServerSchemaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentSchema _currentSchema;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerSchemaConnectionInterceptor"/> class.
    /// </summary>
    /// <param name="currentSchema">The current schema accessor.</param>
    public SqlServerSchemaConnectionInterceptor(ICurrentSchema currentSchema)
    {
        _currentSchema = currentSchema;
    }

    /// <inheritdoc />
    public async override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection.GetType().Name == "SqlConnection" &&
            !string.IsNullOrWhiteSpace(_currentSchema.Name))
        {
            await using var cmd = connection.CreateCommand();
            // Set the default schema for this session
            cmd.CommandText = $"SET SCHEMA '{_currentSchema.Name}';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection.GetType().Name == "SqlConnection" &&
            !string.IsNullOrWhiteSpace(_currentSchema.Name))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET SCHEMA '{_currentSchema.Name}';";
            cmd.ExecuteNonQuery();
        }

        base.ConnectionOpened(connection, eventData);
    }
}

