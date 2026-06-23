using System.Data.Common;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class NpgsqlAetherProvider(
    SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal) : IAetherDatabaseProvider
{
    private readonly SchemaSwitchingMode _mode = mode;

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
    {
        builder.UseNpgsql(sharedConnection);
        builder.AddInterceptors(new SearchPathCommandInterceptor(schema, state, _mode));
    }

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString);
}
