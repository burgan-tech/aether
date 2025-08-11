using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Interceptors;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherEfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddAetherDbContext<TDbContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : DbContext
    {
        services.AddSingleton<AuditInterceptor>();
        services.AddDbContext<TDbContext>((sp, dbContextOptions) =>
        {
            options?.Invoke(dbContextOptions);
            dbContextOptions.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>()
            );
        });

        services.AddScoped<ITransactionService, EfCoreTransactionService<TDbContext>>();
        return services;
    }
}