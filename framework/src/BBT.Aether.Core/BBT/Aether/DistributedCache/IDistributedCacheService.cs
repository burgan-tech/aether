using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.DistributedCache;

/// <summary>
/// Provides an abstraction for distributed caching operations
/// </summary>
public interface IDistributedCacheService
{
    /// <summary>
    /// Get or set a value in the cache, fetching from the source if not present
    /// </summary>
    Task<T?> GetOrSetAsync<T>(
        string cacheKey,
        Func<Task<T>> fetchFunc,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;


    /// <summary>
    /// Get or set a value in the cache, fetching from the source if not present
    /// </summary>
    Task<T?> GetOrSetAsync<TKey, T>(
        TKey key,
        Func<TKey, Task<T>> fetchFunc,
        Func<TKey, string>? keySelector = null,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Get a value from the cache
    /// </summary>
    /// <typeparam name="T">Type of the cached item</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached value or default</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Set a value in the cache
    /// </summary>
    /// <typeparam name="T">Type of the item to cache</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="value">Value to cache</param>
    /// <param name="options">Cache entry options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Remove an item from the cache
    /// </summary>
    /// <param name="key">Cache key to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refresh the cache entry to reset its sliding expiration
    /// </summary>
    /// <param name="key">Cache key to refresh</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RefreshAsync(string key, CancellationToken cancellationToken = default);
}