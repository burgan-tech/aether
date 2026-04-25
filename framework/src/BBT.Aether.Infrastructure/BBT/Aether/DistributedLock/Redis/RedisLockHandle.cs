using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BBT.Aether.DistributedLock.Redis;

public sealed class RedisLockHandle(
    IDatabase database,
    string resourceId,
    string lockOwner,
    ILogger logger)
    : IAsyncDisposable, IDisposable
{
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
    }

    public void Dispose()
    {
        ReleaseAsync().GetAwaiter().GetResult();
    }

    private async ValueTask ReleaseAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "DistributedLock.Release",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "redis");
        activity?.SetTag("lock.resource_id", resourceId);

        try
        {
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    return redis.call('DEL', KEYS[1])
                else
                    return 0
                end";

            var result = (long)await database.ScriptEvaluateAsync(
                script,
                keys: [resourceId],
                values: [lockOwner]);

            if (result > 0)
            {
                logger.LogDebug(
                    "Released Redis lock for resource {ResourceId} with owner {LockOwner}",
                    resourceId, lockOwner);
                activity?.SetTag("lock.released", true);
            }
            else
            {
                logger.LogDebug(
                    "No Redis lock to release or owner mismatch for resource {ResourceId} with owner {LockOwner}",
                    resourceId, lockOwner);
                activity?.SetTag("lock.released", false);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error releasing Redis lock for resource {ResourceId} with owner {LockOwner}",
                resourceId, lockOwner);

            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
                    { "exception.message", ex.Message },
                }));
            }
        }
    }
}
