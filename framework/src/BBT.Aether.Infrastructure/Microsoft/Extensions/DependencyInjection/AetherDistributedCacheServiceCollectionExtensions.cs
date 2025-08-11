using System;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedCache.Dapr;
using BBT.Aether.DistributedCache.Redis;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherDistributedCacheServiceCollectionExtensions
{
    /// <summary>
    /// Add .NET Core Distributed Cache Service
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to</param>
    /// <param name="configureCache">Optional action to configure the distributed cache implementation</param>
    public static IServiceCollection AddNetCoreDistributedCache(
        this IServiceCollection services,
        Action<IServiceCollection>? configureCache = null)
    {
        // Allow external configuration of distributed cache
        configureCache?.Invoke(services);

        // Register the cache service
        services.AddScoped<IDistributedCacheService, NetCoreDistributedCacheService>();

        return services;
    }

    /// <summary>
    /// Add Dapr Distributed Cache Service
    /// </summary>
    public static IServiceCollection AddDaprDistributedCache(
        this IServiceCollection services,
        string storeName)
    {
        if (storeName.IsNullOrWhiteSpace())
        {
            throw new ArgumentNullException($"Dapr Distributed Cache {nameof(storeName)} cannot be null or empty.");
        }

        // Register the Dapr State Store cache service
        services.AddScoped<IDistributedCacheService>(sp =>
            new DaprDistributedCacheService(
                sp.GetRequiredService<DaprClient>(),
                storeName
            )
        );

        return services;
    }

    /// <summary>
    /// Add Redis Distributed Cache Service
    /// </summary>
    public static IServiceCollection AddRedisDistributedCache(this IServiceCollection services)
    {
        // Register the Redis cache service
        services.AddScoped<IDistributedCacheService, RedisDistributedCacheService>();

        return services;
    }
}