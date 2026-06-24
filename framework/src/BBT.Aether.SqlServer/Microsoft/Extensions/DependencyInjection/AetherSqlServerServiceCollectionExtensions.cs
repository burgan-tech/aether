using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherSqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Aether DbContext backed by SQL Server (single-schema).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddAetherSqlServer&lt;MyDbContext&gt;(connectionString);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherSqlServer<TDbContext>(
        this IServiceCollection services, string connectionString,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
        => services.AddAetherDbContext<TDbContext>(new SqlServerAetherProvider(), connectionString, configure);
}
