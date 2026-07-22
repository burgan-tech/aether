using System;
using System.Data.Common;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        provider.ApplyShared(
            builder,
            sharedConnection,
            schema,
            state,
            serviceProvider.GetRequiredService<ICurrentSchema>());
        return builder.Options;
    }

    public DbContextOptions<TDbContext> BuildOwnedOptions(string schema)
    {
        var builder = new DbContextOptionsBuilder<TDbContext>();
        configure(serviceProvider, builder);
        provider.ApplyOwned(
            builder,
            connectionString,
            schema,
            serviceProvider.GetRequiredService<ICurrentSchema>());
        return builder.Options;
    }
}
