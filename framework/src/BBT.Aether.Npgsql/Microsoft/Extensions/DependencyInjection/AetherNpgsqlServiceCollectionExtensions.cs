using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherNpgsqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Aether DbContext backed by PostgreSQL (Npgsql).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherNpgsql<TDbContext>(
        this IServiceCollection services, string connectionString,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
        => services.AddAetherDbContext<TDbContext>(new NpgsqlAetherProvider(), connectionString, configure);
}
