using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Dapr.Client;
using Microsoft.Extensions.Logging;

#pragma warning disable CS0618 // Type or member is obsolete

namespace BBT.Aether.DistributedLock.Dapr;

/// <summary>
/// Implementation of distributed lock using Dapr
/// </summary>
public class DaprDistributedLockService(
    DaprClient daprClient,
    ILogger<DaprDistributedLockService> logger,
    IApplicationInfoAccessor applicationInfoAccessor,
    string storeName)
    : IDistributedLockService
{
    public async Task<IAsyncDisposable?> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartLockActivity("DistributedLock.Acquire", resourceId, expiryInSeconds);

        try
        {
            var lockOwner = $"{GetClientIdentifier()}";
            var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockOwner, expiryInSeconds, cancellationToken);
            if (resourceLock != null && resourceLock.Success)
            {
                logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
                activity?.SetTag("lock.acquired", true);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return resourceLock;
            }

            logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring lock for resource {ResourceId}", resourceId);
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

        activity?.SetTag("lock.provider", "dapr");
        activity?.SetTag("lock.resource_id", resourceId);
        activity?.SetTag("lock.store_name", storeName);

        try
        {
            var lockOwner = $"{GetClientIdentifier()}";
            await daprClient.Unlock(storeName, resourceId, lockOwner, cancellationToken);
            activity?.SetTag("lock.released", true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
            return false;
        }
    }

    public async Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartLockActivity("DistributedLock.Execute", resourceId, expiryInSeconds);

        try
        {
            var lockOwner = $"{GetClientIdentifier()}";
            await using var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockOwner, expiryInSeconds, cancellationToken);
            if (resourceLock == null || !resourceLock.Success)
            {
                logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
                activity?.SetTag("lock.acquired", false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return (false, default);
            }

            logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", true);
            var result = await function();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return (true, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing function with Dapr lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
            throw;
        }
    }

    public async Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartLockActivity("DistributedLock.Execute", resourceId, expiryInSeconds);

        try
        {
            var lockOwner = $"{GetClientIdentifier()}";
            await using var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockOwner, expiryInSeconds, cancellationToken);
            if (resourceLock == null || !resourceLock.Success)
            {
                logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
                activity?.SetTag("lock.acquired", false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return false;
            }

            logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
            activity?.SetTag("lock.acquired", true);
            await action();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action with Dapr lock for resource {ResourceId}", resourceId);
            RecordException(activity, ex);
            throw;
        }
    }

    private Activity? StartLockActivity(string operationName, string resourceId, int expiryInSeconds)
    {
        var activity = InfrastructureActivitySource.Source.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("lock.provider", "dapr");
        activity?.SetTag("lock.resource_id", resourceId);
        activity?.SetTag("lock.store_name", storeName);
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
            ($"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.{applicationInfoAccessor.ApplicationName}")
            .ToLowerInvariant();
    }
}
