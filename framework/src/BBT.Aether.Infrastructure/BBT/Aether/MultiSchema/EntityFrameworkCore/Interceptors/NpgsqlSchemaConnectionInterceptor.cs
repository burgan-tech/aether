using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.MultiSchema.EntityFrameworkCore.Interceptors;

/// <summary>
/// Connection interceptor for PostgreSQL that sets the search_path to the current schema
/// when a connection is opened.
/// Requires Npgsql package to be installed.
/// </summary>
public sealed class NpgsqlSchemaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlSchemaConnectionInterceptor"/> class.
    /// </summary>
    public NpgsqlSchemaConnectionInterceptor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        
        if (connection.GetType().Name == "NpgsqlConnection" &&
            !string.IsNullOrWhiteSpace(currentSchema.Name))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET search_path = \"{currentSchema.Name}\";";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var scope = _scopeFactory.CreateScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        
        if (connection.GetType().Name == "NpgsqlConnection" &&
            !string.IsNullOrWhiteSpace(currentSchema.Name))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET search_path = \"{currentSchema.Name}\";";
            cmd.ExecuteNonQuery();
        }

        base.ConnectionOpened(connection, eventData);
    }
}

