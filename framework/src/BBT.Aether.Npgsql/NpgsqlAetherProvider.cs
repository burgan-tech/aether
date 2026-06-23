using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class NpgsqlAetherProvider(
    SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal) : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
    {
        builder.UseNpgsql(sharedConnection);
        builder.AddInterceptors(new SearchPathCommandInterceptor(schema, state, mode));

        if (mode == SchemaSwitchingMode.SessionSearchPath)
        {
            // Register once per UoW (??= is idempotent across multiple DbContext creations).
            // CompositeUnitOfWork calls this before disposing the connection.
            state.Cleanup ??= static async (conn, ct) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "RESET search_path";
                await cmd.ExecuteNonQueryAsync(ct);
            };
        }
    }

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString);
}
