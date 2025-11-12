# Distributed Lock

## Overview

Distributed Lock in Aether provides a mechanism to coordinate work across multiple application instances. It ensures only one instance processes a critical section at a time, preventing race conditions and duplicate processing in distributed environments.

## Key Features

- **Multiple Providers** - Redis and Dapr implementations
- **Automatic Cleanup** - Locks expire automatically
- **ExecuteWithLock Pattern** - Convenient execution wrapper
- **Lock Renewal** - Extend lock duration
- **Owner Identification** - Track lock ownership

## Core Interface

### IDistributedLockService

```csharp
public interface IDistributedLockService
{
    Task<bool> TryAcquireLockAsync(
        string resourceId, 
        int expiryInSeconds = 60, 
        CancellationToken cancellationToken = default);
    
    Task<bool> ReleaseLockAsync(
        string resourceId, 
        CancellationToken cancellationToken = default);
    
    Task<T?> ExecuteWithLockAsync<T>(
        string resourceId, 
        Func<Task<T>> function, 
        int expiryInSeconds = 60, 
        CancellationToken cancellationToken = default);
    
    Task<bool> ExecuteWithLockAsync(
        string resourceId, 
        Func<Task> action, 
        int expiryInSeconds = 60, 
        CancellationToken cancellationToken = default);
}
```

## Configuration

### Redis Lock

```csharp
services.AddRedisDistributedLock();

// Requires Redis connection
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    return ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis"));
});
```

### Dapr Lock

```csharp
services.AddDaprDistributedLock("lockstore");

// Requires Dapr sidecar with lock component
```

## Usage Examples

### Basic Lock Acquisition

```csharp
public class OrderService
{
    private readonly IDistributedLockService _lockService;
    
    public async Task ProcessOrderAsync(Guid orderId)
    {
        var resourceId = $"order:{orderId}";
        
        // Try to acquire lock
        var lockAcquired = await _lockService.TryAcquireLockAsync(resourceId, expiryInSeconds: 30);
        
        if (!lockAcquired)
        {
            _logger.LogWarning("Could not acquire lock for order {OrderId}", orderId);
            return;
        }
        
        try
        {
            // Critical section
            await ProcessOrderLogicAsync(orderId);
        }
        finally
        {
            // Always release lock
            await _lockService.ReleaseLockAsync(resourceId);
        }
    }
}
```

### ExecuteWithLock Pattern

```csharp
public async Task ProcessOrderAsync(Guid orderId)
{
    var executed = await _lockService.ExecuteWithLockAsync(
        resourceId: $"order:{orderId}",
        action: async () =>
        {
            await ProcessOrderLogicAsync(orderId);
        },
        expiryInSeconds: 30
    );
    
    if (!executed)
    {
        _logger.LogWarning("Could not acquire lock for order {OrderId}", orderId);
    }
}
```

### ExecuteWithLock with Return Value

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
            return new ProcessResult { Success = true, OrderId = orderId };
        },
        expiryInSeconds: 60
    );
}
```

### Preventing Duplicate Processing

```csharp
public class PaymentProcessor
{
    public async Task ProcessPaymentAsync(Guid paymentId)
    {
        var executed = await _lockService.ExecuteWithLockAsync(
            resourceId: $"payment:{paymentId}",
            action: async () =>
            {
                // Check if already processed
                var payment = await _repository.GetAsync(paymentId);
                if (payment.IsProcessed)
                    return;
                
                // Process payment
                await ProcessPaymentLogicAsync(payment);
                payment.MarkAsProcessed();
                await _repository.UpdateAsync(payment);
            },
            expiryInSeconds: 120
        );
    }
}
```

### Background Job Coordination

```csharp
public class ReportGenerator
{
    public async Task GenerateDailyReportAsync()
    {
        // Ensure only one instance generates the report
        var executed = await _lockService.ExecuteWithLockAsync(
            resourceId: "daily-report-generation",
            action: async () =>
            {
                _logger.LogInformation("Generating daily report...");
                await GenerateReportAsync();
                _logger.LogInformation("Daily report generated successfully");
            },
            expiryInSeconds: 300 // 5 minutes
        );
        
        if (!executed)
        {
            _logger.LogInformation("Another instance is generating the report");
        }
    }
}
```

## Best Practices

### 1. Choose Appropriate Lock Duration

```csharp
// ✅ Good: Lock duration matches operation time
await _lockService.ExecuteWithLockAsync(
    "quick-operation",
    async () => await QuickOperationAsync(),
    expiryInSeconds: 10
);

await _lockService.ExecuteWithLockAsync(
    "long-operation",
    async () => await LongOperationAsync(),
    expiryInSeconds: 300
);
```

### 2. Use Descriptive Resource IDs

```csharp
// ✅ Good: Clear, structured resource IDs
var lockId = $"order:payment:{orderId}";
var lockId = $"user:profile:update:{userId}";
var lockId = $"report:daily:{date:yyyy-MM-dd}";

// ❌ Bad: Vague or inconsistent
var lockId = orderId.ToString();
var lockId = "lock1";
```

### 3. Handle Lock Failures Gracefully

```csharp
// ✅ Good: Handle failure appropriately
var executed = await _lockService.ExecuteWithLockAsync(resourceId, async () =>
{
    await ProcessAsync();
});

if (!executed)
{
    // Queue for retry or notify
    await _queue.EnqueueRetryAsync(resourceId);
}

// ❌ Bad: Ignoring lock failure
await _lockService.ExecuteWithLockAsync(resourceId, async () =>
{
    await ProcessAsync();
});
// Continues regardless of success
```

### 4. Keep Critical Sections Short

```csharp
// ✅ Good: Only lock what needs coordination
await _lockService.ExecuteWithLockAsync(
    $"inventory:{productId}",
    async () =>
    {
        // Only critical inventory update
        var product = await _repository.GetAsync(productId);
        product.DecreaseStock(quantity);
        await _repository.UpdateAsync(product);
    }
);

// Do non-critical work outside lock
await SendNotificationAsync(productId);

// ❌ Bad: Locking too much
await _lockService.ExecuteWithLockAsync(
    $"inventory:{productId}",
    async () =>
    {
        var product = await _repository.GetAsync(productId);
        product.DecreaseStock(quantity);
        await _repository.UpdateAsync(product);
        await SendNotificationAsync(productId); // Don't need lock for this
    }
);
```

## Common Patterns

### Cache Stampede Prevention

```csharp
public async Task<Product> GetProductAsync(Guid id)
{
    // Try cache first
    var cached = await _cache.GetAsync<Product>($"product:{id}");
    if (cached != null)
        return cached;
    
    // Use lock to prevent multiple simultaneous DB queries
    return await _lockService.ExecuteWithLockAsync(
        resourceId: $"cache:product:{id}",
        function: async () =>
        {
            // Check cache again (might have been populated while waiting for lock)
            cached = await _cache.GetAsync<Product>($"product:{id}");
            if (cached != null)
                return cached;
            
            // Fetch from DB and cache
            var product = await _repository.GetAsync(id);
            await _cache.SetAsync($"product:{id}", product);
            return product;
        },
        expiryInSeconds: 10
    ) ?? throw new EntityNotFoundException(typeof(Product), id);
}
```

### Singleton Background Task

```csharp
public class CleanupJob
{
    public async Task ExecuteAsync()
    {
        await _lockService.ExecuteWithLockAsync(
            resourceId: "cleanup-job",
            action: async () =>
            {
                await CleanupExpiredDataAsync();
            },
            expiryInSeconds: 600 // 10 minutes
        );
    }
}
```

## Testing

```csharp
public class OrderServiceTests
{
    private readonly Mock<IDistributedLockService> _mockLockService;
    
    [Fact]
    public async Task ProcessOrder_ShouldProcess_WhenLockAcquired()
    {
        // Arrange
        _mockLockService
            .Setup(l => l.ExecuteWithLockAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<int>(),
                default))
            .Returns<string, Func<Task>, int, CancellationToken>(
                async (_, action, _, _) =>
                {
                    await action();
                    return true;
                });
        
        // Act
        await _service.ProcessOrderAsync(orderId);
        
        // Assert
        _mockRepository.Verify(r => r.UpdateAsync(It.IsAny<Order>(), false, default), Times.Once);
    }
}
```

## Related Features

- **[Distributed Cache](../distributed-cache/README.md)** - Coordinate cache updates
- **[Background Jobs](../background-job/README.md)** - Coordinate job execution

