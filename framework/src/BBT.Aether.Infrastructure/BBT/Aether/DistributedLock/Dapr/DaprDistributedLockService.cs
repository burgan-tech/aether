using System;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task<bool> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lockId = $"{GetClientIdentifier()}-{resourceId}";
#pragma warning disable CS0618 // Type or member is obsolete
            await using var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockId, expiryInSeconds, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete
            if (resourceLock.Success)
            {
                logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
                return true;
            }

            logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    public async Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var lockId = $"{GetClientIdentifier()}-{resourceId}";
#pragma warning disable CS0618 // Type or member is obsolete
            await daprClient.Unlock(storeName, resourceId, lockId, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    public async Task<T?> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lockId = $"{GetClientIdentifier()}-{resourceId}";
#pragma warning disable CS0618 // Type or member is obsolete
            await using var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockId, expiryInSeconds, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete
            if (!resourceLock.Success)
            {
                logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
                return default;
            }

            logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
            return await function();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing function with lock for resource {ResourceId}", resourceId);
            return default;
        }
    }

    public async Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lockId = $"{GetClientIdentifier()}-{resourceId}";
#pragma warning disable CS0618 // Type or member is obsolete
            await using var resourceLock =
                await daprClient.Lock(storeName, resourceId, lockId, expiryInSeconds, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete
            if (!resourceLock.Success)
            {
                logger.LogWarning("Failed to acquire lock for resource {ResourceId}", resourceId);
                return false;
            }

            logger.LogDebug("Successfully acquired lock for resource {ResourceId}", resourceId);
            await action();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action with lock for resource {ResourceId}", resourceId);
            return false;
        }
    }

    private string GetClientIdentifier()
    {
        return
            $"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}-{applicationInfoAccessor.ApplicationName}";
    }
}