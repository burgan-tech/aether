using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BBT.Aether.DistributedCache.Redis;

/// <summary>
/// Implementation of distributed cache using Redis
/// </summary>
public class RedisDistributedCacheService(
    IConnectionMultiplexer redisConnection,
    ILogger<RedisDistributedCacheService> logger)
    : DistributedCacheBase
{
    private readonly IConnectionMultiplexer _redisConnection = redisConnection ?? throw new ArgumentNullException(nameof(redisConnection));
    private readonly ILogger<RedisDistributedCacheService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public async override Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        using var activity = StartCacheActivity("DistributedCache.Get", key);

        try
        {
            var database = _redisConnection.GetDatabase();
            var cachedValue = await database.StringGetAsync(key);

            if (!cachedValue.HasValue)
            {
                _logger.LogDebug("Cache miss for key: {Key}", key);
                activity?.SetTag("cache.hit", false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return null;
            }

            var result = JsonSerializer.Deserialize<T>((string)cachedValue!, _jsonOptions);
            _logger.LogDebug("Cache hit for key: {Key}", key);
            activity?.SetTag("cache.hit", true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting value from Redis cache for key: {Key}", key);
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

        try
        {
            var database = _redisConnection.GetDatabase();
            var serializedValue = JsonSerializer.Serialize(value, _jsonOptions);

            TimeSpan? expiry = null;

            if (options?.AbsoluteExpiration.HasValue == true)
            {
                expiry = options.AbsoluteExpiration.Value - DateTimeOffset.UtcNow;
            }
            else if (options?.SlidingExpiration.HasValue == true)
            {
                expiry = options.SlidingExpiration.Value;
            }

            if (expiry.HasValue)
            {
                activity?.SetTag("cache.ttl_seconds", (int)expiry.Value.TotalSeconds);
            }

            await database.StringSetAsync(key, serializedValue, expiry, keepTtl: false);
            _logger.LogDebug("Successfully cached value for key: {Key} with expiry: {Expiry}", key, expiry);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting value in Redis cache for key: {Key}", key);
            RecordException(activity, ex);
            throw;
        }
    }

    public async override Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = StartCacheActivity("DistributedCache.Remove", key);

        try
        {
            var database = _redisConnection.GetDatabase();
            var removed = await database.KeyDeleteAsync(key);
            
            if (removed)
            {
                _logger.LogDebug("Successfully removed key from cache: {Key}", key);
            }
            else
            {
                _logger.LogDebug("Key not found in cache: {Key}", key);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing key from Redis cache: {Key}", key);
            RecordException(activity, ex);
            throw;
        }
    }

    public async override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = StartCacheActivity("DistributedCache.Refresh", key);

        try
        {
            var database = _redisConnection.GetDatabase();
            var exists = await database.KeyExistsAsync(key);
            
            if (exists)
            {
                await database.KeyTouchAsync(key);
                _logger.LogDebug("Successfully refreshed key: {Key}", key);
            }
            else
            {
                _logger.LogDebug("Key does not exist for refresh: {Key}", key);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing key in Redis cache: {Key}", key);
            RecordException(activity, ex);
            throw;
        }
    }

    private static Activity? StartCacheActivity(string operationName, string key)
    {
        var activity = InfrastructureActivitySource.Source.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("cache.provider", "redis");
        activity?.SetTag("cache.key", key);

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
