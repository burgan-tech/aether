# Inbox & Outbox Pattern

## Overview

The Inbox and Outbox patterns ensure reliable message delivery in distributed systems. The Outbox pattern guarantees events are published even if the message broker is temporarily unavailable, while the Inbox pattern ensures idempotent message processing.

## Key Features

- **Transactional Outbox** - Events stored in database with business data
- **Background Processing** - Automatic retry and delivery
- **Inbox for Idempotency** - Prevent duplicate message processing
- **EF Core Integration** - Seamless database integration
- **Configurable Retention** - Automatic cleanup of old messages

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

### IOutboxStore

```csharp
public interface IOutboxStore
{
    Task SaveAsync(OutboxMessage message, CancellationToken cancellationToken = default);
    Task<List<OutboxMessage>> GetUnprocessedAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task UpdateRetryInfoAsync(Guid messageId, string error, DateTime nextRetryAt, CancellationToken cancellationToken = default);
}
```

## Configuration

### DbContext Setup

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>, IHasOutbox, IHasInbox
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

### Service Registration

```csharp
// Register Outbox
services.AddAetherOutbox<MyDbContext>(options =>
{
    options.ProcessingInterval = TimeSpan.FromSeconds(30);
    options.MaxRetryCount = 5;
    options.RetryDelay = TimeSpan.FromMinutes(1);
    options.CleanupRetentionDays = 7;
});

// Register Inbox
services.AddAetherInbox<MyDbContext>(options =>
{
    options.CleanupRetentionDays = 30;
});
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

The `OutboxProcessor` automatically processes messages:

```csharp
// Runs in background
public class OutboxProcessor<TDbContext> : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessOutboxMessagesAsync(stoppingToken);
            await CleanupProcessedMessagesAsync(stoppingToken);
            await Task.Delay(_options.ProcessingInterval, stoppingToken);
        }
    }
}
```

### Inbox: Idempotent Handler

```csharp
public class OrderCreatedEventHandler : IDistributedEventHandler<OrderCreatedEvent>
{
    private readonly IInboxStore _inboxStore;
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    [UnitOfWork]
    public async Task HandleEventAsync(
        OrderCreatedEvent @event, 
        CancellationToken cancellationToken)
    {
        // Extract message ID from Dapr headers
        var messageId = _httpContextAccessor.HttpContext?.Request.Headers["cloudevents-id"].ToString();
        
        if (string.IsNullOrEmpty(messageId))
        {
            messageId = Guid.NewGuid().ToString();
        }
        
        // Check if already processed
        var inboxMessage = await _inboxStore.FindAsync(messageId, cancellationToken);
        if (inboxMessage != null)
        {
            _logger.LogInformation("Message {MessageId} already processed, skipping", messageId);
            return;
        }
        
        try
        {
            // Process the event
            await ProcessOrderAsync(@event, cancellationToken);
            
            // Mark as processed in inbox
            await _inboxStore.InsertAsync(new InboxMessage
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                EventName = "order.created.v1",
                ReceivedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow,
                Status = InboxMessageStatus.Processed
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {MessageId}", messageId);
            
            // Mark as failed
            await _inboxStore.InsertAsync(new InboxMessage
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                EventName = "order.created.v1",
                ReceivedAt = DateTime.UtcNow,
                Status = InboxMessageStatus.Failed,
                ErrorMessage = ex.Message
            }, cancellationToken);
            
            throw;
        }
    }
    
    private async Task ProcessOrderAsync(OrderCreatedEvent @event, CancellationToken ct)
    {
        // Business logic here
        var order = await _orderRepository.GetAsync(@event.OrderId, cancellationToken: ct);
        // ...
    }
}
```

## Inbox Pattern

### InboxMessage Entity

```csharp
public class InboxMessage : Entity<Guid>
{
    public string MessageId { get; set; } // Unique message ID from broker
    public string EventName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public InboxMessageStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public ExtraPropertyDictionary ExtraProperties { get; private set; }
}

public enum InboxMessageStatus
{
    Received = 0,
    Processed = 1,
    Failed = 2
}
```

### IInboxStore

```csharp
public interface IInboxStore
{
    Task<InboxMessage?> FindAsync(string messageId, CancellationToken cancellationToken = default);
    Task InsertAsync(InboxMessage message, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken = default);
}
```

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
    Id UUID PRIMARY KEY,
    MessageId VARCHAR(256) NOT NULL UNIQUE,
    EventName VARCHAR(500) NOT NULL,
    ReceivedAt TIMESTAMP NOT NULL,
    ProcessedAt TIMESTAMP NULL,
    Status INT NOT NULL,
    ErrorMessage VARCHAR(4000) NULL,
    ExtraProperties JSONB NULL
);

CREATE INDEX IX_InboxMessages_MessageId ON InboxMessages(MessageId);
CREATE INDEX IX_InboxMessages_Cleanup ON InboxMessages(ProcessedAt, ReceivedAt);
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
```

### 2. Implement Inbox for All Handlers

```csharp
// ✅ Good: Idempotent processing
public async Task HandleEventAsync(MyEvent @event, CancellationToken ct)
{
    if (await _inbox.ExistsAsync(messageId, ct))
        return;
    
    await ProcessAsync(@event, ct);
    await _inbox.MarkProcessedAsync(messageId, ct);
}
```

### 3. Monitor Outbox Processing

```csharp
// Monitor failed messages
var failedMessages = await _outboxStore.GetFailedMessagesAsync();
foreach (var message in failedMessages)
{
    _logger.LogWarning("Failed to process outbox message {Id}: {Error}", 
        message.Id, message.LastError);
}
```

### 4. Configure Appropriate Retention

```csharp
services.AddAetherOutbox<MyDbContext>(options =>
{
    options.CleanupRetentionDays = 7; // Keep processed messages for 7 days
});
```

## Monitoring & Troubleshooting

### Check Outbox Status

```csharp
public class OutboxMonitor
{
    public async Task<OutboxStats> GetStatsAsync()
    {
        var unprocessed = await _context.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .CountAsync();
        
        var failed = await _context.OutboxMessages
            .Where(m => m.RetryCount >= _options.MaxRetryCount)
            .CountAsync();
        
        return new OutboxStats { Unprocessed = unprocessed, Failed = failed };
    }
}
```

### Retry Failed Messages

```csharp
public async Task RetryFailedMessagesAsync()
{
    var failedMessages = await _context.OutboxMessages
        .Where(m => m.ProcessedAt == null && m.RetryCount >= _options.MaxRetryCount)
        .ToListAsync();
    
    foreach (var message in failedMessages)
    {
        message.RetryCount = 0;
        message.NextRetryAt = DateTime.UtcNow;
        message.LastError = null;
    }
    
    await _context.SaveChangesAsync();
}
```

## Related Features

- **[Distributed Events](../distributed-events/README.md)** - Event bus integration
- **[Domain Events](../domain-events/README.md)** - Domain event dispatching
- **[Unit of Work](../unit-of-work/README.md)** - Transaction management

