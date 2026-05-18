# Distributed Lock

## Overview

Coordinates work across multiple application instances to prevent race conditions and duplicate processing. Supports Redis and Dapr implementations with automatic lock expiry and scope-based lock lifecycle management.

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

### ExecuteWithLock Pattern (Recommended for Short Operations)

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
    var (acquired, result) = await _lockService.ExecuteWithLockAsync(
        resourceId: $"order:{orderId}",
        function: async () =>
        {
            var order = await _repository.GetAsync(orderId);
            order.Process();
            await _repository.UpdateAsync(order);
            return new ProcessResult { Success = true };
        },
        expiryInSeconds: 60);

    return acquired ? result : null;
}
```

### Scope-Based Lock Management (Handle API)

Use when you need to hold a lock across multiple operations, extend TTL, or control the lock lifecycle explicitly:

```csharp
public async Task ProcessPipelineAsync(Guid pipelineId, CancellationToken ct)
{
    await using var lockHandle = await _lockService.TryAcquireLockAsync(
        $"pipeline:{pipelineId}", expiryInSeconds: 60, ct);

    if (lockHandle is null)
    {
        _logger.LogWarning("Pipeline {Id} is already running", pipelineId);
        return;
    }

    // Step 1
    await ExecuteStepOneAsync(ct);

    // Extend TTL before a long step
    await lockHandle.ExtendAsync(120, ct);

    // Step 2 (long-running)
    await ExecuteStepTwoAsync(ct);

    // Lock is automatically released when lockHandle is disposed
}
```

### Explicit Release

```csharp
await using var lockHandle = await _lockService.TryAcquireLockAsync("resource:123", 30);
if (lockHandle is null) return;

try
{
    await DoWorkAsync();
}
finally
{
    // Explicit release (DisposeAsync also releases if not already done)
    await lockHandle.ReleaseAsync();
}
```

## Interface

```csharp
public interface IDistributedLockService
{
    Task<IDistributedLockHandle?> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60, CancellationToken ct = default);
    Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60, CancellationToken ct = default);
    Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60, CancellationToken ct = default);
}

public interface IDistributedLockHandle : IAsyncDisposable
{
    string LockKey { get; }
    string Owner { get; }
    Task<bool> ExtendAsync(int leaseSeconds, CancellationToken ct = default);
    Task ReleaseAsync(CancellationToken ct = default);
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
    
    var (acquired, product) = await _lockService.ExecuteWithLockAsync(
        $"cache:product:{id}",
        async () =>
        {
            cached = await _cache.GetAsync<Product>($"product:{id}");
            if (cached != null) return cached;
            
            var p = await _repository.GetAsync(id);
            await _cache.SetAsync($"product:{id}", p);
            return p;
        },
        expiryInSeconds: 10);

    return product ?? throw new EntityNotFoundException(typeof(Product), id);
}
```

### Long-Running Pipeline with TTL Extension

```csharp
public async Task RunMigrationAsync(CancellationToken ct)
{
    await using var lockHandle = await _lockService.TryAcquireLockAsync(
        "schema-migration", expiryInSeconds: 120, ct);

    if (lockHandle is null)
    {
        _logger.LogInformation("Migration is already running on another instance");
        return;
    }

    foreach (var step in migrationSteps)
    {
        await lockHandle.ExtendAsync(120, ct);
        await step.ExecuteAsync(ct);
    }
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

## Tracing

All lock operations automatically emit OpenTelemetry spans via the `BBT.Aether.Infrastructure` ActivitySource. No additional configuration is needed when using `AddAetherTelemetry`.

Span names:
- `DistributedLock.Acquire` - lock acquisition attempts
- `DistributedLock.Release` - explicit or dispose-based lock release
- `DistributedLock.Extend` - TTL extension via handle
- `DistributedLock.Execute` - `ExecuteWithLockAsync` (covers acquire + execute + release)

Tags added to each span:

| Tag | Description |
|-----|-------------|
| `lock.provider` | `"dapr"` or `"redis"` |
| `lock.resource_id` | The resource identifier being locked |
| `lock.store_name` | Dapr component name (Dapr provider only) |
| `lock.expiry_seconds` | Lock TTL |
| `lock.acquired` | Whether the lock was successfully obtained |
| `lock.released` | Whether the lock was successfully released |
| `lock.extended` | Whether the TTL was successfully extended |

Example trace hierarchy:

```
[ASP.NET Core] POST /api/orders
  [BBT.Aether.Aspects] OrderService.ProcessOrder
    [BBT.Aether.Infrastructure] DistributedLock.Acquire
    [BBT.Aether.Infrastructure] DistributedLock.Extend
    [BBT.Aether.Infrastructure] DistributedLock.Release
```

## Best Practices

1. **Choose appropriate lock duration** - Match to expected operation time + buffer
2. **Use descriptive resource IDs** - `order:payment:{orderId}`, `report:daily:{date}`
3. **Handle lock failures** - Log, queue for retry, or skip gracefully
4. **Keep critical sections short** - Do non-critical work outside the lock
5. **Prefer ExecuteWithLock for short operations** - Automatic release on completion/exception
6. **Use Handle API for long-running scopes** - Call `ExtendAsync` to prevent TTL expiry
7. **Always use `await using`** - Guarantees lock release even on exceptions

## Related Features

- [Distributed Cache](../distributed-cache/README.md) - Cache stampede prevention
- [Background Jobs](../background-job/README.md) - Coordinate job execution
- [OpenTelemetry](../telemetry/README.md) - Tracing configuration
