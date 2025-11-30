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
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
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
// Absolute - Expires at specific time
new DistributedCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(1)
}

// Relative - Expires after duration
new DistributedCacheEntryOptions
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
}

// Sliding - Resets on access
new DistributedCacheEntryOptions
{
    SlidingExpiration = TimeSpan.FromMinutes(15)
}
```

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
