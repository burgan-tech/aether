using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BBT.Aether.DistributedLock.Redis;

/// <summary>
/// Implementation of distributed lock using Redis
/// </summary>
public class RedisDistributedLockService(
    IConnectionMultiplexer redisConnection,
    ILogger<RedisDistributedLockService> logger,
    IApplicationInfoAccessor applicationInfoAccessor)
    : IDistributedLockService
{
    public async Task<bool> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = redisConnection.GetDatabase();
            var lockKey = $"distributed-lock:{resourceId}";
            var lockValue = $"{GetClientIdentifier()}-{resourceId}";
            var expiry = TimeSpan.FromSeconds(expiryInSeconds);

            // Use atomic SETNX (SET if Not eXists) with expiry
            var acquired = await database.StringSetAsync(
                lockKey,
                lockValue,
                expiry,
                When.NotExists
            );

            if (acquired)
            {
                logger.LogDebug("Successfully acquired Redis lock for resource {ResourceId}", resourceId);
                return true;
            }

            logger.LogWarning("Failed to acquire Redis lock for resource {ResourceId}", resourceId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring Redis lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    public async Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = redisConnection.GetDatabase();
            var lockKey = $"distributed-lock:{resourceId}";

            // Use Lua script to ensure atomic delete only if the lock exists
            var script = @"
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('del', KEYS[1])
                else
                    return 0
                end";

            var lockValue = await database.StringGetAsync(lockKey);
            if (lockValue.IsNullOrEmpty)
            {
                logger.LogWarning("Attempting to release non-existent lock for resource {ResourceId}", resourceId);
                return false;
            }

            var result = await database.ScriptEvaluateAsync(script,
                [lockKey],
                [lockValue]
            );

            var released = (int)result == 1;

            if (released)
            {
                logger.LogDebug("Successfully released Redis lock for resource {ResourceId}", resourceId);
            }
            else
            {
                logger.LogWarning("Failed to release Redis lock for resource {ResourceId}", resourceId);
            }

            return released;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing Redis lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    public async Task<T?> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60, CancellationToken cancellationToken = default)
    {
        if (function == null)
        {
            throw new ArgumentNullException(nameof(function));
        }

        var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (!lockAcquired)
        {
            logger.LogWarning("Could not acquire lock for resource {ResourceId}", resourceId);
            return default;
        }

        try
        {
            return await function();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing function with Redis lock for resource {ResourceId}", resourceId);
            throw;
        }
        finally
        {
            await ReleaseLockAsync(resourceId, cancellationToken);
        }
    }

    public async Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60, CancellationToken cancellationToken = default)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (!lockAcquired)
        {
            logger.LogWarning("Could not acquire lock for resource {ResourceId}", resourceId);
            return false;
        }

        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action with Redis lock for resource {ResourceId}", resourceId);
            throw;
        }
        finally
        {
            await ReleaseLockAsync(resourceId, cancellationToken);
        }
    }
    
    private string GetClientIdentifier()
    {
        return $"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}-{applicationInfoAccessor.ApplicationName}";
    }
}