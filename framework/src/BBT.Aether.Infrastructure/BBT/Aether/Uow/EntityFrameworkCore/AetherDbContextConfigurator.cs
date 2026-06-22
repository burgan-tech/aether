using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class AetherDbContextConfigurator<TDbContext>(
    string connectionString,
    Action<IServiceProvider, DbContextOptionsBuilder> configure,
    IServiceProvider serviceProvider)
    : IAetherDbContextConfigurator<TDbContext>
    where TDbContext : DbContext
{
    public string ConnectionString => connectionString;

    public DbContextOptions<TDbContext> BuildOptions(DbConnection sharedConnection)
    {
        var builder = new DbContextOptionsBuilder<TDbContext>();
        // Apply the registered configuration (interceptors, provider tuning, etc.)...
        configure(serviceProvider, builder);
        // ...then bind to the shared connection. UseNpgsql(connection) overrides any
        // connection-string-based provider call made by `configure`, while keeping
        // interceptors/options added via AddInterceptors.
        builder.UseNpgsql(sharedConnection);
        return builder.Options;
    }
}
