# Inbox & Outbox Pattern

## Overview

The Inbox and Outbox patterns ensure reliable message delivery in distributed systems. The Outbox pattern guarantees events are published even if the message broker is temporarily unavailable, while the Inbox pattern ensures idempotent message processing with error tracking and retry capabilities.

## Key Features

- **Transactional Outbox** - Events stored in database with business data
- **Background Processing** - Automatic retry and delivery via processor interfaces
- **Inbox for Idempotency** - Prevent duplicate message processing
- **Error Tracking** - Failed events are tracked with retry information
- **Extensible Architecture** - Processors implement interfaces, developers control hosting
- **EF Core Integration** - Seamless database integration
- **Configurable Retention** - Automatic cleanup of old messages

## Architecture

### Processor Interfaces

Processors implement interfaces rather than inheriting from BackgroundService, giving developers full control over hosting and lifecycle management.

```csharp
public interface IOutboxProcessor
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

public interface IInboxProcessor
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
```

### Store Interfaces

Store interfaces provide abstraction for data access operations:

```csharp
public interface IOutboxStore
{
    Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
    Task<List<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, int maxRetryCount, CancellationToken cancellationToken = default);
    Task<List<OutboxMessage>> GetProcessedMessagesForCleanupAsync(DateTime cutoffDate, int batchSize, CancellationToken cancellationToken = default);
    Task UpdateAsync(OutboxMessage message, CancellationToken cancellationToken = default);
    Task DeleteRangeAsync(List<OutboxMessage> messages, CancellationToken cancellationToken = default);
}

public interface IInboxStore
{
    Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
    Task<InboxMessage?> GetByIdAsync(string eventId, CancellationToken cancellationToken = default);
    Task<List<InboxMessage>> GetMessagesForCleanupAsync(DateTime cutoffDate, int batchSize, CancellationToken cancellationToken = default);
    Task InsertAsync(InboxMessage message, CancellationToken cancellationToken = default);
    Task UpdateAsync(InboxMessage message, CancellationToken cancellationToken = default);
    Task DeleteRangeAsync(List<InboxMessage> messages, CancellationToken cancellationToken = default);
}
```

## Outbox Pattern

### OutboxMessage Entity

```csharp
public class OutboxMessage : Entity<Guid>
{
    public string EventName { get; private set; }
    public byte[] EventData { get; private set; } // Serialized CloudEventEnvelope
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public ExtraPropertyDictionary ExtraProperties { get; private set; }
}
```

## Inbox Pattern

### InboxMessage Entity

```csharp
public class InboxMessage : Entity<string>
{
    public string EventName { get; private set; }
    public byte[] EventData { get; private set; } // Serialized CloudEventEnvelope
    public DateTime CreatedAt { get; set; }
    public IncomingEventStatus Status { get; set; } // Pending, Processed, Discarded
    public DateTime? HandledTime { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; }
    public ExtraPropertyDictionary ExtraProperties { get; private set; }
    
    public void MarkAsProcessed(DateTime processedTime);
    public void MarkAsDiscarded(DateTime discardedTime);
    public void RetryLater(int retryCount, DateTime nextRetryTime);
}
```

## Configuration

### DbContext Setup

```csharp
using BBT.Aether.Persistence;

public class MyDbContext : AetherDbContext<MyDbContext>, IHasEfCoreOutbox, IHasEfCoreInbox
{
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<InboxMessage> InboxMessages { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ConfigureOutbox();
        modelBuilder.ConfigureInbox();
    }
}
```

**Note**: `IHasEfCoreOutbox` and `IHasEfCoreInbox` are marker interfaces defined in the Infrastructure layer (`BBT.Aether.Persistence` namespace) to maintain clean architecture principles. The Domain layer remains persistence-ignorant and doesn't have any direct dependency on Entity Framework Core.

### Service Registration

```csharp
// Register Outbox
services.AddSingleton<AetherOutboxOptions>(new AetherOutboxOptions
{
    ProcessingInterval = TimeSpan.FromSeconds(30),
    MaxRetryCount = 5,
    RetryBaseDelay = TimeSpan.FromMinutes(1),
    RetentionPeriod = TimeSpan.FromDays(7),
    BatchSize = 100
});

services.AddScoped<IOutboxStore, EfCoreOutboxStore<MyDbContext>>();
services.AddSingleton<IOutboxProcessor, OutboxProcessor<MyDbContext>>();

// Register Inbox
services.AddSingleton<AetherInboxOptions>(new AetherInboxOptions
{
    RetentionPeriod = TimeSpan.FromDays(7),
    CleanupInterval = TimeSpan.FromHours(1),
    CleanupBatchSize = 1000,
    MaxRetryCount = 5,
    RetryBaseDelay = TimeSpan.FromMinutes(1),
    ProcessingInterval = TimeSpan.FromSeconds(30)
});

services.AddScoped<IInboxStore, EfCoreInboxStore<MyDbContext>>();
services.AddSingleton<IInboxProcessor, InboxProcessor<MyDbContext>>();
```

## Usage Examples

### Outbox: Publishing Events

```csharp
[UnitOfWork]
public async Task CreateOrderAsync(CreateOrderDto dto)
{
    var order = new Order(dto);
    await _orderRepository.InsertAsync(order);
    
    // Publish with outbox (stored in same transaction)
    await _eventBus.PublishAsync(
        new OrderCreatedEvent(order.Id), 
        useOutbox: true);
    
    // On commit:
    // 1. Order is saved
    // 2. Event is written to OutboxMessages table
    // 3. Background processor will publish it
}
```

### Outbox: Background Processing

Developers implement their own BackgroundService to host the processor:

```csharp
public class OutboxBackgroundService : BackgroundService
{
    private readonly IOutboxProcessor _processor;
    private readonly ILogger<OutboxBackgroundService> _logger;

    public OutboxBackgroundService(
        IOutboxProcessor processor,
        ILogger<OutboxBackgroundService> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox background service starting");
        
        try
        {
            await _processor.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outbox background service failed");
            throw;
        }
        
        _logger.LogInformation("Outbox background service stopped");
    }
}

// Register the BackgroundService
services.AddHostedService<OutboxBackgroundService>();
```

### Inbox: Background Processing

Similarly, implement BackgroundService for inbox cleanup:

```csharp
public class InboxBackgroundService : BackgroundService
{
    private readonly IInboxProcessor _processor;
    private readonly ILogger<InboxBackgroundService> _logger;

    public InboxBackgroundService(
        IInboxProcessor processor,
        ILogger<InboxBackgroundService> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inbox background service starting");
        
        try
        {
            await _processor.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inbox background service failed");
            throw;
        }
        
        _logger.LogInformation("Inbox background service stopped");
    }
}

// Register the BackgroundService
services.AddHostedService<InboxBackgroundService>();
```

### Inbox: Automatic Idempotency with EventsController

The `EventsController` automatically handles idempotency checking and error tracking:

```csharp
[ApiController]
[Route("api/events")]
public class MyEventsController : EventsController
{
    public MyEventsController(
        IDistributedEventInvokerRegistry invokerRegistry,
        IInboxStore inboxStore,
        IUnitOfWorkManager unitOfWorkManager,
        IServiceProvider serviceProvider,
        IEventSerializer serializer,
        AetherInboxOptions inboxOptions,
        ILogger<EventsController> logger)
        : base(invokerRegistry, inboxStore, unitOfWorkManager, 
               serviceProvider, serializer, inboxOptions, logger)
    {
    }

    [HttpPost("{name}/v{version}")]
    public async Task<IActionResult> HandleEvent(
        string name, 
        int version, 
        CancellationToken cancellationToken)
    {
        return await ProcessEventAsync(name, version, cancellationToken);
    }
}
```

**Event Processing Flow:**
1. Receives event from Dapr
2. Checks inbox for duplicate (idempotency)
3. Invokes registered event handler
4. Marks as processed in inbox on success
5. Tracks error with retry info in inbox on failure
6. Returns 500 to trigger Dapr retry

**Error Tracking:**
When event processing fails:
- Creates/updates InboxMessage with error details
- Stores error message in `ExtraProperties["LastError"]`
- Calculates next retry time with exponential backoff
- Sets status to `Pending` for potential retry
- Still returns HTTP 500 to let Dapr handle retries

## Database Schema

### Outbox Table

```sql
CREATE TABLE OutboxMessages (
    Id UUID PRIMARY KEY,
    EventName VARCHAR(500) NOT NULL,
    EventData BYTEA NOT NULL,
    CreatedAt TIMESTAMP NOT NULL,
    ProcessedAt TIMESTAMP NULL,
    RetryCount INT NOT NULL DEFAULT 0,
    LastError VARCHAR(4000) NULL,
    NextRetryAt TIMESTAMP NULL,
    ExtraProperties JSONB NULL
);

CREATE INDEX IX_OutboxMessages_Processing 
    ON OutboxMessages(ProcessedAt, NextRetryAt, RetryCount);
```

### Inbox Table

```sql
CREATE TABLE InboxMessages (
    Id VARCHAR(256) PRIMARY KEY,
    EventName VARCHAR(500) NOT NULL,
    EventData BYTEA NOT NULL,
    CreatedAt TIMESTAMP NOT NULL,
    Status INT NOT NULL,
    HandledTime TIMESTAMP NULL,
    RetryCount INT NOT NULL DEFAULT 0,
    NextRetryTime TIMESTAMP NULL,
    ExtraProperties JSONB NULL
);

CREATE INDEX IX_InboxMessages_Status ON InboxMessages(Status);
CREATE INDEX IX_InboxMessages_Cleanup ON InboxMessages(Status, HandledTime);
```

## Configuration Options

### AetherOutboxOptions

```csharp
public class AetherOutboxOptions
{
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public int BatchSize { get; set; } = 100;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
}
```

### AetherInboxOptions

```csharp
public class AetherInboxOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public int CleanupBatchSize { get; set; } = 1000;
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromSeconds(30);
}
```

## Best Practices

### 1. Always Use Outbox for Critical Events

```csharp
// ✅ Good: Transactional consistency
[UnitOfWork]
public async Task ProcessOrderAsync()
{
    await _repository.SaveAsync(order);
    await _eventBus.PublishAsync(event, useOutbox: true);
}

// ❌ Bad: Event might be lost if broker is down
[UnitOfWork]
public async Task ProcessOrderAsync()
{
    await _repository.SaveAsync(order);
    await _eventBus.PublishAsync(event, useOutbox: false);
}
```

### 2. Let EventsController Handle Idempotency

The framework automatically handles idempotency checking and error tracking:

```csharp
// ✅ Good: Framework handles it automatically
[ApiController]
[Route("api/events")]
public class MyEventsController : EventsController
{
    [HttpPost("{name}/v{version}")]
    public async Task<IActionResult> HandleEvent(string name, int version, CancellationToken ct)
    {
        return await ProcessEventAsync(name, version, ct);
    }
}
```

### 3. Implement BackgroundService Wrappers

```csharp
// ✅ Good: Custom BackgroundService with logging and error handling
public class OutboxBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _processor.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Outbox processor crashed");
            // Notify operations team
            throw;
        }
    }
}
```

### 4. Monitor Failed Messages

```csharp
public class OutboxMonitor
{
    public async Task<OutboxStats> GetStatsAsync()
    {
        var unprocessed = await _outboxStore.GetUnprocessedMessagesAsync(1000, 5);
        var failedCount = unprocessed.Count(m => m.RetryCount >= 5);
        
        return new OutboxStats 
        { 
            Unprocessed = unprocessed.Count, 
            Failed = failedCount 
        };
    }
}
```

### 5. Configure Appropriate Retention

```csharp
services.AddSingleton<AetherOutboxOptions>(new AetherOutboxOptions
{
    RetentionPeriod = TimeSpan.FromDays(7), // Keep for compliance/debugging
    MaxRetryCount = 5, // Prevent infinite retry loops
    RetryBaseDelay = TimeSpan.FromMinutes(1) // Start with 1 min delay
});
```

## Monitoring & Troubleshooting

### Check Outbox Status

```csharp
public class OutboxHealthCheck : IHealthCheck
{
    private readonly IOutboxStore _store;
    private readonly AetherOutboxOptions _options;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        var messages = await _store.GetUnprocessedMessagesAsync(100, _options.MaxRetryCount, ct);
        var failedCount = messages.Count(m => m.RetryCount >= _options.MaxRetryCount);
        
        if (failedCount > 10)
        {
            return HealthCheckResult.Unhealthy($"{failedCount} messages have exceeded max retry count");
        }
        
        if (messages.Count > 1000)
        {
            return HealthCheckResult.Degraded($"{messages.Count} unprocessed messages in outbox");
        }
        
        return HealthCheckResult.Healthy($"{messages.Count} messages pending");
    }
}
```

### Retry Failed Messages

```csharp
public class OutboxManagementService
{
    public async Task RetryFailedMessagesAsync()
    {
        var failedMessages = await _outboxStore.GetUnprocessedMessagesAsync(100, int.MaxValue);
        
        foreach (var message in failedMessages.Where(m => m.RetryCount >= _options.MaxRetryCount))
        {
            message.RetryCount = 0;
            message.NextRetryAt = DateTime.UtcNow;
            message.LastError = null;
            
            await _outboxStore.UpdateAsync(message);
        }
    }
}
```

## Advanced Scenarios

### Custom Error Tracking

Override `TryTrackErrorInInboxAsync` in EventsController:

```csharp
protected override async Task TryTrackErrorInInboxAsync(
    byte[] payload, 
    string? eventId, 
    Exception exception, 
    CancellationToken cancellationToken)
{
    // Custom error tracking logic
    await base.TryTrackErrorInInboxAsync(payload, eventId, exception, cancellationToken);
    
    // Send alert for critical errors
    if (exception is CriticalBusinessException)
    {
        await _alertService.SendAlertAsync($"Critical error processing event {eventId}");
    }
}
```

### Custom Retry Strategy

Override `CalculateNextRetryTime` in EventsController:

```csharp
protected override DateTime CalculateNextRetryTime(int retryCount)
{
    // Linear backoff instead of exponential
    var delay = InboxOptions.RetryBaseDelay * retryCount;
    return DateTime.UtcNow.Add(delay);
}
```

### Processor Control

Control processor behavior at runtime:

```csharp
public class ManagedOutboxService : BackgroundService
{
    private readonly IOutboxProcessor _processor;
    private CancellationTokenSource? _processorCts;
    
    public void Pause()
    {
        _processorCts?.Cancel();
    }
    
    public void Resume()
    {
        _processorCts = new CancellationTokenSource();
        Task.Run(() => _processor.RunAsync(_processorCts.Token));
    }
}
```

## Related Features

- **[Distributed Events](../distributed-events/README.md)** - Event bus integration
- **[Domain Events](../domain-events/README.md)** - Domain event dispatching
- **[Unit of Work](../unit-of-work/README.md)** - Transaction management
- **[Telemetry](../telemetry/README.md)** - Distributed tracing for events
