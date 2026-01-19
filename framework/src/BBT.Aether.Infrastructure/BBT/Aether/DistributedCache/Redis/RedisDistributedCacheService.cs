using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        try
        {
            var database = _redisConnection.GetDatabase();
            var cachedValue = await database.StringGetAsync(key);

            if (!cachedValue.HasValue)
            {
                _logger.LogDebug("Cache miss for key: {Key}", key);
                return null;
            }

            var result = JsonSerializer.Deserialize<T>((string)cachedValue!, _jsonOptions);
            _logger.LogDebug("Cache hit for key: {Key}", key);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting value from Redis cache for key: {Key}", key);
            return null;
        }
    }

    public async override Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
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

            await database.StringSetAsync(key, serializedValue, expiry, keepTtl: false);
            _logger.LogDebug("Successfully cached value for key: {Key} with expiry: {Expiry}", key, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting value in Redis cache for key: {Key}", key);
            throw;
        }
    }

    public async override Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing key from Redis cache: {Key}", key);
            throw;
        }
    }

    public async override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _redisConnection.GetDatabase();
            
            // Redis doesn't have a direct refresh mechanism like SQL Server
            // We'll implement it by checking if the key exists and touching it
            var exists = await database.KeyExistsAsync(key);
            
            if (exists)
            {
                // Touch the key to refresh its expiration
                await database.KeyTouchAsync(key);
                _logger.LogDebug("Successfully refreshed key: {Key}", key);
            }
            else
            {
                _logger.LogDebug("Key does not exist for refresh: {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing key in Redis cache: {Key}", key);
            throw;
        }
    }
} 