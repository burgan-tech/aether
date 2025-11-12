# Distributed Cache

## Overview

Aether provides a unified abstraction for distributed caching with support for multiple providers including Redis, Dapr State Store, and .NET Core IDistributedCache. The consistent API ensures easy switching between providers without code changes.

## Key Features

- **Provider Abstraction** - Single interface for all cache providers
- **Multiple Implementations** - Redis, Dapr, .NET Core
- **GetOrSet Pattern** - Automatic cache-aside implementation
- **Expiration Policies** - Absolute and sliding expiration
- **JSON Serialization** - Automatic object serialization
- **Async API** - Full async/await support

## Core Interface

### IDistributedCacheService

```csharp
public interface IDistributedCacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) 
        where T : class;
    
    Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;
    
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    
    Task RefreshAsync(string key, CancellationToken cancellationToken = default);
    
    Task<T?> GetOrSetAsync<T>(
        string cacheKey,
        Func<Task<T>> fetchFunc,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;
}
```

### DistributedCacheEntryOptions

```csharp
public class DistributedCacheEntryOptions
{
    public DateTimeOffset? AbsoluteExpiration { get; set; }
    public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
    public TimeSpan? SlidingExpiration { get; set; }
}
```

## Configuration

### Redis Cache

```csharp
services.AddRedisDistributedCache();

// Requires Redis connection configuration
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis"));
});
```

### Dapr State Store

```csharp
services.AddDaprDistributedCache("statestore");

// Requires Dapr sidecar with state store component
```

### .NET Core IDistributedCache

```csharp
services.AddNetCoreDistributedCache(services =>
{
    services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = configuration.GetConnectionString("Redis");
    });
});
```

## Usage Examples

### Basic Get/Set

```csharp
public class ProductService
{
    private readonly IDistributedCacheService _cache;
    
    public async Task<Product?> GetProductAsync(Guid id)
    {
        var cacheKey = $"product:{id}";
        
        // Try get from cache
        var cached = await _cache.GetAsync<Product>(cacheKey);
        if (cached != null)
            return cached;
        
        // Fetch from database
        var product = await _repository.GetAsync(id);
        
        // Store in cache
        await _cache.SetAsync(cacheKey, product, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
        });
        
        return product;
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
        }
    );
}
```

### Cache Invalidation

```csharp
public async Task UpdateProductAsync(Guid id, UpdateProductDto dto)
{
    var product = await _repository.GetAsync(id);
    product.Update(dto.Name, dto.Price);
    await _repository.UpdateAsync(product);
    
    // Invalidate cache
    await _cache.RemoveAsync($"product:{id}");
}
```

### Caching Lists

```csharp
public async Task<List<Product>> GetProductsByCategoryAsync(string category)
{
    return await _cache.GetOrSetAsync(
        $"products:category:{category}",
        async () => await _repository.GetListAsync(p => p.Category == category),
        new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        }
    );
}
```

### Expiration Strategies

```csharp
// Absolute expiration - expires at specific time
await _cache.SetAsync(key, value, new DistributedCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(1)
});

// Relative expiration - expires after duration
await _cache.SetAsync(key, value, new DistributedCacheEntryOptions
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
});

// Sliding expiration - resets on access
await _cache.SetAsync(key, value, new DistributedCacheEntryOptions
{
    SlidingExpiration = TimeSpan.FromMinutes(15)
});
```

## Best Practices

### 1. Use Consistent Key Naming

```csharp
// ✅ Good: Structured key naming
private string GetCacheKey(string prefix, Guid id) => $"{prefix}:{id}";

var key = GetCacheKey("product", productId);
var key = GetCacheKey("user", userId);

// ❌ Bad: Inconsistent keys
await _cache.GetAsync<Product>($"prod{id}");
await _cache.GetAsync<Product>($"{id}_product");
```

### 2. Handle Cache Misses Gracefully

```csharp
// ✅ Good: Fallback to source
public async Task<Product> GetProductAsync(Guid id)
{
    var cached = await _cache.GetAsync<Product>($"product:{id}");
    if (cached != null)
        return cached;
    
    return await _repository.GetAsync(id);
}

// ✅ Better: Use GetOrSet
return await _cache.GetOrSetAsync(
    $"product:{id}",
    () => _repository.GetAsync(id)
);
```

### 3. Choose Appropriate Expiration

```csharp
// Frequently changing data - short expiration
await _cache.SetAsync(key, stockPrice, new()
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
});

// Rarely changing data - longer expiration
await _cache.SetAsync(key, categories, new()
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
});

// Session data - sliding expiration
await _cache.SetAsync(key, session, new()
{
    SlidingExpiration = TimeSpan.FromMinutes(20)
});
```

### 4. Invalidate on Updates

```csharp
[UnitOfWork]
public async Task UpdateProductAsync(Guid id, UpdateProductDto dto)
{
    var product = await _repository.GetAsync(id);
    product.Update(dto);
    await _repository.UpdateAsync(product);
    
    // Invalidate related caches
    await _cache.RemoveAsync($"product:{id}");
    await _cache.RemoveAsync($"products:category:{product.Category}");
}
```

## Testing

```csharp
public class ProductServiceTests
{
    private readonly Mock<IDistributedCacheService> _mockCache;
    private readonly Mock<IRepository<Product, Guid>> _mockRepository;
    private readonly ProductService _service;
    
    [Fact]
    public async Task GetProduct_ShouldReturnFromCache_WhenCached()
    {
        // Arrange
        var product = new Product(Guid.NewGuid(), "Test");
        _mockCache
            .Setup(c => c.GetAsync<Product>(It.IsAny<string>(), default))
            .ReturnsAsync(product);
        
        // Act
        var result = await _service.GetProductAsync(product.Id);
        
        // Assert
        Assert.Equal(product.Id, result.Id);
        _mockRepository.Verify(r => r.GetAsync(It.IsAny<Guid>(), true, default), Times.Never);
    }
}
```

## Related Features

- **[Distributed Lock](../distributed-lock/README.md)** - Coordinate cache updates
- **[Repository Pattern](../repository-pattern/README.md)** - Data source for cache

