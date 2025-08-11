using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using DomainDistributedCacheEntryOptions = BBT.Aether.DistributedCache.DistributedCacheEntryOptions;
using MicrosoftDistributedCacheEntryOptions = Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions;

namespace BBT.Aether.DistributedCache;

/// <summary>
/// Implementation of distributed cache using .NET Core's IDistributedCache
/// </summary>
public class NetCoreDistributedCacheService(IDistributedCache distributedCache) : DistributedCacheBase
{
    private readonly IDistributedCache _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public async override Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        var cachedValue = await _distributedCache.GetStringAsync(key, cancellationToken);

        if (string.IsNullOrEmpty(cachedValue))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(cachedValue, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async override Task SetAsync<T>(
        string key,
        T value,
        DomainDistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var serializedValue = JsonSerializer.Serialize(value, _jsonOptions);

        var cacheOptions = new MicrosoftDistributedCacheEntryOptions();

        if (options?.AbsoluteExpiration.HasValue == true)
        {
            cacheOptions.SetAbsoluteExpiration(options.AbsoluteExpiration.Value);
        }

        if (options?.SlidingExpiration.HasValue == true)
        {
            cacheOptions.SetSlidingExpiration(options.SlidingExpiration.Value);
        }

        await _distributedCache.SetStringAsync(key, serializedValue, cacheOptions, cancellationToken);
    }

    public override Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _distributedCache.RemoveAsync(key, cancellationToken);
    }

    public override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        return _distributedCache.RefreshAsync(key, cancellationToken);
    }
}