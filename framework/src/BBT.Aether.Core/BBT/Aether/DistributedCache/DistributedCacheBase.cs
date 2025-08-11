using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.DistributedCache;

/// <summary>
/// Abstract base class for distributed cache implementations
/// </summary>
public abstract class DistributedCacheBase : IDistributedCacheService
{
    /// <summary>
    /// Get a value from the cache
    /// </summary>
    public abstract Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Set a value in the cache
    /// </summary>
    public abstract Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Remove an item from the cache
    /// </summary>
    public abstract Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refresh the cache entry to reset its sliding expiration
    /// </summary>
    public abstract Task RefreshAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get or set a value in the cache, fetching from the source if not present
    /// </summary>
    public virtual async Task<T?> GetOrSetAsync<T>(
        string cacheKey,
        Func<Task<T>> fetchFunc,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        // Try to get from cache first
        var cachedValue = await GetAsync<T>(cacheKey, cancellationToken);

        if (cachedValue != null)
            return cachedValue;

        // If not in cache, fetch from the provided function
        var freshValue = await fetchFunc();

        // Cache the value with default or provided options
        options ??= DistributedCacheEntryOptions.WithSlidingExpiration(TimeSpan.FromMinutes(30));
        await SetAsync(cacheKey, freshValue, options, cancellationToken);

        return freshValue;
    }

    /// <summary>
    /// Get or set a value in the cache, fetching from the source if not present
    /// </summary>
    public virtual async Task<T?> GetOrSetAsync<TKey, T>(
        TKey key,
        Func<TKey, Task<T>> fetchFunc,
        Func<TKey, string>? keySelector = null,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        // Use provided key selector or default to ToString
        var cacheKey = keySelector?.Invoke(key) ?? key?.ToString() ?? throw new ArgumentNullException(nameof(key));

        // Try to get from cache first
        var cachedValue = await GetAsync<T>(cacheKey, cancellationToken);

        if (cachedValue != null)
            return cachedValue;

        // If not in cache, fetch from the provided function
        var freshValue = await fetchFunc(key);

        // Cache the value with default or provided options
        options ??= DistributedCacheEntryOptions.WithSlidingExpiration(TimeSpan.FromMinutes(30));
        await SetAsync(cacheKey, freshValue, options, cancellationToken);

        return freshValue;
    }
}