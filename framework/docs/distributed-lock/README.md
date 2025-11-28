# Distributed Lock

## Overview

Coordinates work across multiple application instances to prevent race conditions and duplicate processing. Supports Redis and Dapr implementations with automatic lock expiry.

## Quick Start

### Redis Lock

```csharp
services.AddRedisDistributedLock();

services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")));
```

### Dapr Lock

```csharp
services.AddDaprDistributedLock("lockstore");
```

## Usage

### ExecuteWithLock Pattern (Recommended)

```csharp
public async Task ProcessOrderAsync(Guid orderId)
{
    var executed = await _lockService.ExecuteWithLockAsync(
        resourceId: $"order:{orderId}",
        action: async () =>
        {
            var order = await _repository.GetAsync(orderId);
            await ProcessOrderLogicAsync(order);
        },
        expiryInSeconds: 30);
    
    if (!executed)
    {
        _logger.LogWarning("Could not acquire lock for order {OrderId}", orderId);
    }
}
```

### With Return Value

```csharp
public async Task<ProcessResult?> ProcessOrderAsync(Guid orderId)
{
    return await _lockService.ExecuteWithLockAsync(
        resourceId: $"order:{orderId}",
        function: async () =>
        {
            var order = await _repository.GetAsync(orderId);
            order.Process();
            await _repository.UpdateAsync(order);
            return new ProcessResult { Success = true };
        },
        expiryInSeconds: 60);
}
```

### Manual Lock Management

```csharp
public async Task ProcessPaymentAsync(Guid paymentId)
{
    var resourceId = $"payment:{paymentId}";
    
    if (!await _lockService.TryAcquireLockAsync(resourceId, expiryInSeconds: 30))
    {
        _logger.LogWarning("Could not acquire lock");
        return;
    }
    
    try
    {
        await ProcessPaymentLogicAsync(paymentId);
    }
    finally
    {
        await _lockService.ReleaseLockAsync(resourceId);
    }
}
```

## Interface

```csharp
public interface IDistributedLockService
{
    Task<bool> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60, CancellationToken ct = default);
    Task<bool> ReleaseLockAsync(string resourceId, CancellationToken ct = default);
    Task<T?> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60, CancellationToken ct = default);
    Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60, CancellationToken ct = default);
}
```

## Common Patterns

### Prevent Duplicate Processing

```csharp
public async Task ProcessPaymentAsync(Guid paymentId)
{
    await _lockService.ExecuteWithLockAsync(
        $"payment:{paymentId}",
        async () =>
        {
            var payment = await _repository.GetAsync(paymentId);
            if (payment.IsProcessed) return; // Already done
            
            await ProcessPaymentLogicAsync(payment);
            payment.MarkAsProcessed();
            await _repository.UpdateAsync(payment);
        },
        expiryInSeconds: 120);
}
```

### Cache Stampede Prevention

```csharp
public async Task<Product> GetProductAsync(Guid id)
{
    var cached = await _cache.GetAsync<Product>($"product:{id}");
    if (cached != null) return cached;
    
    return await _lockService.ExecuteWithLockAsync(
        $"cache:product:{id}",
        async () =>
        {
            // Check again after acquiring lock
            cached = await _cache.GetAsync<Product>($"product:{id}");
            if (cached != null) return cached;
            
            var product = await _repository.GetAsync(id);
            await _cache.SetAsync($"product:{id}", product);
            return product;
        },
        expiryInSeconds: 10) ?? throw new EntityNotFoundException(typeof(Product), id);
}
```

### Singleton Background Task

```csharp
public async Task GenerateDailyReportAsync()
{
    var executed = await _lockService.ExecuteWithLockAsync(
        "daily-report-generation",
        async () => await GenerateReportAsync(),
        expiryInSeconds: 300);
    
    if (!executed)
        _logger.LogInformation("Another instance is generating the report");
}
```

## Best Practices

1. **Choose appropriate lock duration** - Match to expected operation time + buffer
2. **Use descriptive resource IDs** - `order:payment:{orderId}`, `report:daily:{date}`
3. **Handle lock failures** - Log, queue for retry, or skip gracefully
4. **Keep critical sections short** - Do non-critical work outside the lock
5. **Prefer ExecuteWithLock** - Automatic release on completion/exception

## Related Features

- [Distributed Cache](../distributed-cache/README.md) - Cache stampede prevention
- [Background Jobs](../background-job/README.md) - Coordinate job execution
