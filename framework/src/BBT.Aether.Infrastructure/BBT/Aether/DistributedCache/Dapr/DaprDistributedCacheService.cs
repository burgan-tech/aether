using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        try
        {
            return await _daprClient.GetStateAsync<T?>(storeName, key, cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async override Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var metadata = new Dictionary<string, string>();

        // Add TTL if specified
        if (options?.AbsoluteExpiration.HasValue == true)
        {
            var ttl = (int)(options.AbsoluteExpiration.Value - DateTimeOffset.UtcNow).TotalSeconds;
            if (ttl > 0)
            {
                metadata["ttlInSeconds"] = ttl.ToString();
            }
        }
        else if (options?.SlidingExpiration.HasValue == true)
        {
            metadata["ttlInSeconds"] = ((int)options.SlidingExpiration.Value.TotalSeconds).ToString();
        }

        await _daprClient.SaveStateAsync(
            storeName,
            key,
            value,
            metadata: metadata,
            cancellationToken: cancellationToken
        );
    }

    public override Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _daprClient.DeleteStateAsync(storeName, key, cancellationToken: cancellationToken);
    }

    public override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        // Dapr doesn't have a direct refresh mechanism, so we'll do nothing
        return Task.CompletedTask;
    }
}