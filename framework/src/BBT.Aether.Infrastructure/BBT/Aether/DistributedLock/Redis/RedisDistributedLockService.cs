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
    public async Task<IAsyncDisposable?> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var database = redisConnection.GetDatabase();
            var lockOwner = GetClientIdentifier();
            var expiry = TimeSpan.FromSeconds(expiryInSeconds);

            // Use atomic SETNX (SET if Not eXists) with expiry
            // Key: resourceId, Value: lockOwner
            var acquired = await database.StringSetAsync(
                resourceId,
                lockOwner,
                expiry,
                When.NotExists
            );

            if (acquired)
            {
                logger.LogDebug("Successfully acquired Redis lock for resource {ResourceId} with owner {LockOwner}",
                    resourceId, lockOwner);
                return new RedisLockHandle(database, resourceId, lockOwner, logger);
            }

            logger.LogWarning("Failed to acquire Redis lock for resource {ResourceId}", resourceId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring Redis lock for resource {ResourceId}", resourceId);
            return null;
        }
    }

    public async Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = redisConnection.GetDatabase();
            var lockOwner = GetClientIdentifier();

            // Use Lua script to ensure atomic delete only if the lock exists and is owned by this client
            var script = @"
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('del', KEYS[1])
                else
                    return 0
                end";

            var result = await database.ScriptEvaluateAsync(script,
                [resourceId],
                [lockOwner]
            );

            var released = (int)result == 1;

            if (released)
            {
                logger.LogDebug("Successfully released Redis lock for resource {ResourceId} with owner {LockOwner}",
                    resourceId, lockOwner);
            }
            else
            {
                logger.LogWarning(
                    "Failed to release Redis lock for resource {ResourceId} - lock not owned by {LockOwner}",
                    resourceId, lockOwner);
            }

            return released;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing Redis lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    public async Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (function == null)
        {
            throw new ArgumentNullException(nameof(function));
        }

        await using var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (lockAcquired == null)
        {
            logger.LogWarning("Could not acquire lock for resource {ResourceId}", resourceId);
            return (false, default);
        }

        try
        {
            var result = await function();
            return (true, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing function with Redis lock for resource {ResourceId}", resourceId);
            throw;
        }
    }

    public async Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        await using var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (lockAcquired == null)
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
    }

    private string GetClientIdentifier()
    {
        return
            ($"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.{applicationInfoAccessor.ApplicationName}.{applicationInfoAccessor.InstanceId}")
            .ToLowerInvariant();
    }
}