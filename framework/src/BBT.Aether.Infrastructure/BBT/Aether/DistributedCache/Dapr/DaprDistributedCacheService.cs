using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Dapr.Client;

namespace BBT.Aether.DistributedCache.Dapr;

/// <summary>
/// Implementation of distributed cache using Dapr State Store
/// </summary>
public class DaprDistributedCacheService(
    DaprClient daprClient,
    string storeName)
    : DistributedCacheBase
{
    private readonly DaprClient _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));

    public async override Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        using var activity = StartCacheActivity("DistributedCache.Get", key);

        try
        {
            var result = await _daprClient.GetStateAsync<T?>(storeName, key, cancellationToken: cancellationToken);
            activity?.SetTag("cache.hit", result != null);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            return null;
        }
    }

    public async override Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        using var activity = StartCacheActivity("DistributedCache.Set", key);

        var metadata = new Dictionary<string, string>();

        if (options?.AbsoluteExpiration.HasValue == true)
        {
            var ttl = (int)(options.AbsoluteExpiration.Value - DateTimeOffset.UtcNow).TotalSeconds;
            if (ttl > 0)
            {
                metadata["ttlInSeconds"] = ttl.ToString();
                activity?.SetTag("cache.ttl_seconds", ttl);
            }
        }
        else if (options?.SlidingExpiration.HasValue == true)
        {
            var ttl = (int)options.SlidingExpiration.Value.TotalSeconds;
            metadata["ttlInSeconds"] = ttl.ToString();
            activity?.SetTag("cache.ttl_seconds", ttl);
        }

        await _daprClient.SaveStateAsync(
            storeName,
            key,
            value,
            metadata: metadata,
            cancellationToken: cancellationToken
        );

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public override async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = StartCacheActivity("DistributedCache.Remove", key);

        await _daprClient.DeleteStateAsync(storeName, key, cancellationToken: cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private Activity? StartCacheActivity(string operationName, string key)
    {
        var activity = InfrastructureActivitySource.Source.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("cache.provider", "dapr");
        activity?.SetTag("cache.key", key);
        activity?.SetTag("cache.store_name", storeName);

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
}
