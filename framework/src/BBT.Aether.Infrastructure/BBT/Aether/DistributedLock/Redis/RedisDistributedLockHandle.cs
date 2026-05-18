using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BBT.Aether.DistributedLock.Redis;

/// <summary>
/// Redis-backed distributed lock handle with extend and explicit release support.
/// </summary>
internal sealed class RedisDistributedLockHandle(
    IDatabase database,
    string lockKey,
    string owner,
    ILogger logger)
    : IDistributedLockHandle, IDisposable
{
    private const string ReleaseScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end";

    private const string ExtendScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end";

    private int _disposed;

    /// <inheritdoc />
    public string LockKey => lockKey;

    /// <inheritdoc />
    public string Owner => owner;

    /// <inheritdoc />
    public async Task<bool> ExtendAsync(int leaseSeconds, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return false;

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "DistributedLock.Extend",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "redis");
        activity?.SetTag("lock.resource_id", lockKey);
        activity?.SetTag("lock.expiry_seconds", leaseSeconds);

        try
        {
            var milliseconds = leaseSeconds * 1000L;
            var result = (long)await database.ScriptEvaluateAsync(
                ExtendScript,
                keys: [(RedisKey)lockKey],
                values: [(RedisValue)owner, milliseconds]);

            var extended = result == 1;

            activity?.SetTag("lock.extended", extended);
            activity?.SetStatus(ActivityStatusCode.Ok);

            if (extended)
            {
                logger.LogDebug("Extended Redis lock TTL for resource {ResourceId} by {LeaseSeconds}s",
                    lockKey, leaseSeconds);
            }
            else
            {
                logger.LogWarning("Failed to extend Redis lock TTL for resource {ResourceId} - lock not owned by {Owner}",
                    lockKey, owner);
            }

            return extended;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extending Redis lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
            RecordException(activity, ex);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "DistributedLock.Release",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "redis");
        activity?.SetTag("lock.resource_id", lockKey);

        try
        {
            var result = (long)await database.ScriptEvaluateAsync(
                ReleaseScript,
                keys: [(RedisKey)lockKey],
                values: [(RedisValue)owner]);

            if (result > 0)
            {
                logger.LogDebug("Released Redis lock for resource {ResourceId} with owner {Owner}",
                    lockKey, owner);
                activity?.SetTag("lock.released", true);
            }
            else
            {
                logger.LogDebug("No Redis lock to release or owner mismatch for resource {ResourceId} with owner {Owner}",
                    lockKey, owner);
                activity?.SetTag("lock.released", false);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing Redis lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
            RecordException(activity, ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(ReleaseAsync());

    /// <inheritdoc />
    public void Dispose() => ReleaseAsync().GetAwaiter().GetResult();

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
}
