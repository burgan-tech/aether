using System;
using BBT.Aether;
using BBT.Aether.DistributedLock;
using BBT.Aether.DistributedLock.Dapr;
using BBT.Aether.DistributedLock.Redis;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherDistributedLockServiceCollectionExtensions
{
    public static IServiceCollection AddRedisDistributedLock(
        this IServiceCollection services)
    {
        services.AddScoped<IDistributedLockService>(sp =>
            new RedisDistributedLockService(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisDistributedLockService>>(),
                sp.GetRequiredService<IApplicationInfoAccessor>()
            )
        );

        return services;
    }
    
    public static IServiceCollection AddDaprDistributedLock(
        this IServiceCollection services,
        string storeName)
    {
        if (storeName.IsNullOrWhiteSpace())
        {
            throw new ArgumentNullException($"Dapr Distributed Lock  {nameof(storeName)} cannot be null or empty.");
        }

        // Register the Dapr State Store cache service
        services.AddScoped<IDistributedLockService>(sp =>
            new DaprDistributedLockService(
                sp.GetRequiredService<DaprClient>(),
                sp.GetRequiredService<ILogger<DaprDistributedLockService>>(),
                sp.GetRequiredService<IApplicationInfoAccessor>(),
                storeName
            )
        );

        return services;
    }
}