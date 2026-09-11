# Distributed Cache

## Overview

Unified caching abstraction with multiple provider support (Redis, Dapr, .NET Core). Provides consistent API for cache operations with GetOrSet pattern and configurable expiration.

## Quick Start

### Redis Cache

```csharp
services.AddRedisDistributedCache();

services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")));
```

### Dapr State Store

```csharp
services.AddDaprDistributedCache("statestore");
```

### .NET Core IDistributedCache

```csharp
services.AddNetCoreDistributedCache(services =>
{
    services.AddStackExchangeRedisCache(options =>
        options.Configuration = configuration.GetConnectionString("Redis"));
});
```

## Usage

### Basic Get/Set

```csharp
public class ProductService
{
    private readonly IDistributedCacheService _cache;
    
    public async Task<Product?> GetProductAsync(Guid id)
    {
        return await _cache.GetAsync<Product>($"product:{id}");
    }
    
    public async Task SetProductAsync(Product product)
    {
        await _cache.SetAsync($"product:{product.Id}", product, 
            new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(30)
            });
    }
}
```

### GetOrSet Pattern

```csharp
public async Task<Product> GetProductAsync(Guid id)
{
    return await _cache.GetOrSetAsync(
        $"product:{id}",
        async () => await _repository.GetAsync(id),
        new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(30)
        });
}
```

### Cache Invalidation

```csharp
public async Task UpdateProductAsync(Product product)
{
    await _repository.UpdateAsync(product);
    await _cache.RemoveAsync($"product:{product.Id}");
}
```

## Expiration Options

```csharp
// Absolute - Expires at a specific point in time
new DistributedCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(1)
}

// Sliding - Expires after a duration of inactivity (see the granularity notes below for Dapr)
new DistributedCacheEntryOptions
{
    SlidingExpiration = TimeSpan.FromMinutes(15)
}
```

### TTL granularity differs by provider

| Provider | Smallest expressible TTL | Notes |
|---|---|---|
| Redis | sub-second | the `TimeSpan` is passed through to `StringSetAsync` |
| .NET Core `IDistributedCache` | sub-second | passed through to `SetAbsoluteExpiration` |
| Dapr state store | **1 second** | Dapr's `ttlInSeconds` metadata is whole seconds |

A request below one second is rounded **up** to one second on Dapr — never down, because a TTL of
zero would make the entry permanent. Do not rely on a sub-second TTL for correctness: if a value
must disappear the moment it goes stale, invalidate it explicitly with `RemoveAsync` and keep the
TTL only as a backstop.

If the requested lifetime has already elapsed (an `AbsoluteExpiration` in the past, or a
non-positive `SlidingExpiration`), the Dapr provider skips the write rather than storing an entry
that would never expire. Any previous value under that key is left in place.

An `AbsoluteExpiration` computed before a slow fetch can elapse before the write happens, and the
write is then skipped — this is exactly what `GetOrSetAsync` does, since it builds `options` before
awaiting `fetchFunc` and only calls `SetAsync` afterwards. Prefer `SlidingExpiration` for short
lifetimes: it is measured at write time rather than against a timestamp fixed earlier, so a slow
fetch cannot make it negative.

`RefreshAsync` on the Dapr provider is a no-op, and Dapr's `ttlInSeconds` metadata does not slide on
access either, so a `SlidingExpiration` behaves as a plain absolute TTL there — it does not reset
when the entry is read.

`cache.ttl_seconds` does not mean the same thing on every provider's span: Redis emits the
*requested* value truncated to whole seconds (`0` for a 500 ms TTL), while Dapr emits the *stored*
value after rounding up (`1` for the same request). Do not aggregate the tag across providers
without accounting for that.

Entries written by an earlier Aether version without a TTL are not repaired by this change and
expire only when overwritten — flush them or key-version them when upgrading.

## Interface

```csharp
public interface IDistributedCacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions? options = null, CancellationToken ct = default) where T : class;
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RefreshAsync(string key, CancellationToken ct = default);
    Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> fetchFunc, DistributedCacheEntryOptions? options = null, CancellationToken ct = default) where T : class;
}
```

## Best Practices

1. **Use consistent key naming** - `{type}:{id}` format (e.g., `product:123`)
2. **Prefer GetOrSet** - Handles cache miss automatically
3. **Choose appropriate expiration** - Sliding for sessions, absolute for data
4. **Invalidate on updates** - Remove stale data after modifications

## Related Features

- [Distributed Lock](../distributed-lock/README.md) - Coordinate cache updates
