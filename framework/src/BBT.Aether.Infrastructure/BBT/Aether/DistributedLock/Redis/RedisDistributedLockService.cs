using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
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
        using var activity = StartLockActivity("DistributedLock.Acquire", resourceId, expiryInSeconds);

        try
        {
            var database = redisConnection.GetDatabase();
            var lockOwner = GetClientIdentifier();
            var expiry = TimeSpan.FromSeconds(expiryInSeconds);

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
                activity?.SetTag("lock.acquired", true);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new RedisLockHandle(database, resourceId, lockOwner, logger);
            }

            logger.LogWarning("Failed to acquire Redis lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring Redis lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
            return null;
        }
    }

    public async Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "DistributedLock.Release",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "redis");
        activity?.SetTag("lock.resource_id", resourceId);

        try
        {
            var database = redisConnection.GetDatabase();
            var lockOwner = GetClientIdentifier();

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

            activity?.SetTag("lock.released", released);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return released;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing Redis lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
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

        using var activity = StartLockActivity("DistributedLock.Execute", resourceId, expiryInSeconds);

        await using var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (lockAcquired == null)
        {
            logger.LogWarning("Could not acquire lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return (false, default);
        }

        activity?.SetTag("lock.acquired", true);

        try
        {
            var result = await function();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return (true, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing function with Redis lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
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

        using var activity = StartLockActivity("DistributedLock.Execute", resourceId, expiryInSeconds);

        await using var lockAcquired = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);

        if (lockAcquired == null)
        {
            logger.LogWarning("Could not acquire lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return false;
        }

        activity?.SetTag("lock.acquired", true);

        try
        {
            await action();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action with Redis lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
            throw;
        }
    }

    private static Activity? StartLockActivity(string operationName, string resourceId, int expiryInSeconds)
    {
        var activity = InfrastructureActivitySource.Source.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "redis");
        activity?.SetTag("lock.resource_id", resourceId);
        activity?.SetTag("lock.expiry_seconds", expiryInSeconds);

        return activity;
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }

    private string GetClientIdentifier()
    {
        return
            ($"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.{applicationInfoAccessor.ApplicationName}.{applicationInfoAccessor.InstanceId}")
            .ToLowerInvariant();
    }
}
