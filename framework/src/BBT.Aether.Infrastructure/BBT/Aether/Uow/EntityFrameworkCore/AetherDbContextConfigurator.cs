using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class AetherDbContextConfigurator<TDbContext>(
    string connectionString,
    IAetherDatabaseProvider provider,
    Action<IServiceProvider, DbContextOptionsBuilder> configure,
    IServiceProvider serviceProvider)
    : IAetherDbContextConfigurator<TDbContext>
    where TDbContext : DbContext
{
    public DbConnection CreateConnection() => provider.CreateConnection(connectionString);

    public DbContextOptions<TDbContext> BuildOptions(DbConnection sharedConnection, string schema, SchemaScopeState state)
    {
        var builder = new DbContextOptionsBuilder<TDbContext>();
        configure(serviceProvider, builder);
        provider.ApplyShared(builder, sharedConnection, schema, state);
        return builder.Options;
    }
}
