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
/// Dapr-backed distributed lock handle with explicit release support.
/// <para>
/// <b>Limitation:</b> <see cref="ExtendAsync"/> is not supported by the Dapr Lock provider.
/// Dapr's Redis lock component uses <c>SET NX</c> (set-if-not-exists) internally,
/// which rejects re-acquire attempts even from the same owner. There is no separate
/// "extend" endpoint in the Dapr Lock API. Use a sufficiently long initial TTL,
/// or switch to <c>AddRedisDistributedLock()</c> which supports atomic TTL extension.
/// </para>
/// </summary>
internal sealed class DaprDistributedLockHandle(
    DaprClient daprClient,
    string storeName,
    string lockKey,
    string owner,
    ILogger logger)
    : IDistributedLockHandle
{
    private int _disposed;

    /// <inheritdoc />
    public string LockKey => lockKey;

    /// <inheritdoc />
    public string Owner => owner;

    /// <summary>
    /// Not supported by the Dapr Lock provider. Always returns <c>false</c>.
    /// Dapr's Redis lock component uses <c>SET NX</c> internally, which does not allow
    /// TTL renewal for an existing lock. Use a sufficiently long initial TTL or switch
    /// to <c>AddRedisDistributedLock()</c> which supports atomic TTL extension via Lua scripts.
    /// </summary>
    public Task<bool> ExtendAsync(int leaseSeconds, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "ExtendAsync is not supported by Dapr Lock provider. " +
            "Lock {ResourceId} TTL was NOT extended. Consider using Redis provider or a longer initial TTL",
            lockKey);

        return Task.FromResult(false);
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

        activity?.SetTag("lock.provider", "dapr");
        activity?.SetTag("lock.resource_id", lockKey);
        activity?.SetTag("lock.store_name", storeName);

        try
        {
            await daprClient.Unlock(storeName, lockKey, owner, cancellationToken);

            activity?.SetTag("lock.released", true);
            activity?.SetStatus(ActivityStatusCode.Ok);

            logger.LogDebug("Released Dapr lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing Dapr lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
            RecordException(activity, ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(ReleaseAsync());

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
