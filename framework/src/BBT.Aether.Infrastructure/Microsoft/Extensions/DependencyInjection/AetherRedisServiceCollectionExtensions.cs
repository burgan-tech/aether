using System;
using System.Collections.Generic;
using BBT.Aether.Configurations;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherRedisServiceCollectionExtensions
{
    public static IServiceCollection AddRedis(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        var redisConfig = configuration.GetSection("Redis").Get<RedisConfiguration>();

        if (redisConfig == null)
        {
            throw new ArgumentNullException($"{nameof(RedisConfiguration)} not found in configuration.");
        }

        var configurationOptions = new ConfigurationOptions
        {
            DefaultDatabase = redisConfig.DefaultDatabase,
            Password = redisConfig.Password,
            Ssl = redisConfig.Ssl,
            ConnectTimeout = redisConfig.ConnectionTimeout,
            AbortOnConnectFail = false
        };

        switch (redisConfig.Mode.ToLower())
        {
            case "standalone":
                foreach (var endpoint in redisConfig.Standalone.EndPoints)
                {
                    configurationOptions.EndPoints.Add(endpoint);
                }

                break;

            case "cluster":
                configurationOptions.CommandMap = CommandMap.Create(new HashSet<string> { "PING" }, false);
                foreach (var endpoint in redisConfig.Cluster.EndPoints)
                {
                    configurationOptions.EndPoints.Add(endpoint);
                }

                break;

            case "sentinel":
                configurationOptions.ServiceName = redisConfig.Sentinel.Masters[0];
                foreach (var sentinel in redisConfig.Sentinel.Sentinels)
                {
                    configurationOptions.EndPoints.Add(sentinel);
                }

                configurationOptions.TieBreaker = "";
                break;

            default:
                throw new ArgumentException($"Unsupported Redis mode: {redisConfig.Mode}");
        }

        var multiplexer = ConnectionMultiplexer.Connect(configurationOptions);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        return services;
    }
}