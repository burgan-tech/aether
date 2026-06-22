using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class NpgsqlAetherProvider : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
    {
        builder.UseNpgsql(sharedConnection);
        builder.AddInterceptors(new SearchPathCommandInterceptor(schema, state));
    }

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString);
}
