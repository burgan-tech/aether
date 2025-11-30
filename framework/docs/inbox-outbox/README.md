# Inbox & Outbox Pattern

## Overview

Ensures reliable message delivery in distributed systems. **Outbox** guarantees events are published even if the broker is down. **Inbox** provides idempotent message processing with retry support.

## Quick Start

### DbContext Setup

```csharp
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

### Service Registration

```csharp
// Outbox
services.AddSingleton<AetherOutboxOptions>(new AetherOutboxOptions
{
    ProcessingInterval = TimeSpan.FromSeconds(30),
    MaxRetryCount = 5,
    RetentionPeriod = TimeSpan.FromDays(7)
});
services.AddScoped<IOutboxStore, EfCoreOutboxStore<MyDbContext>>();
services.AddSingleton<IOutboxProcessor, OutboxProcessor<MyDbContext>>();

// Inbox
services.AddSingleton<AetherInboxOptions>(new AetherInboxOptions
{
    RetentionPeriod = TimeSpan.FromDays(7),
    MaxRetryCount = 5
});
services.AddScoped<IInboxStore, EfCoreInboxStore<MyDbContext>>();
services.AddSingleton<IInboxProcessor, InboxProcessor<MyDbContext>>();
```

### Background Service (Host the processors)

```csharp
public class OutboxBackgroundService : BackgroundService
{
    private readonly IOutboxProcessor _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _processor.RunAsync(stoppingToken);
    }
}

// Register
services.AddHostedService<OutboxBackgroundService>();
services.AddHostedService<InboxBackgroundService>();
```

## Outbox Usage

### Publishing Events with Outbox

```csharp
[UnitOfWork]
public async Task CreateOrderAsync(CreateOrderDto dto)
{
    var order = new Order(dto);
    await _orderRepository.InsertAsync(order);
    
    // Event stored in same transaction as order
    await _eventBus.PublishAsync(
        new OrderCreatedEvent(order.Id), 
        useOutbox: true);
    
    // On commit: Order saved + Event in OutboxMessages
    // Background processor publishes to broker
}
```

### Using Domain Events

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id)); // Auto outbox
    }
}
```

## Inbox Usage

### EventsController (Automatic Idempotency)

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
        ICurrentSchema currentSchema,
        AetherInboxOptions inboxOptions,
        ILogger<EventsController> logger)
        : base(invokerRegistry, inboxStore, unitOfWorkManager, 
               serviceProvider, serializer, inboxOptions, currentSchema, logger)
    { }

    [HttpPost("{name}/v{version}")]
    public async Task<IActionResult> HandleEvent(string name, int version, CancellationToken ct)
    {
        return await ProcessEventAsync(name, version, ct);
    }
}
```

**Processing Flow:**
1. Receives event from Dapr
2. Checks inbox for duplicate (idempotency)
3. Invokes registered handler
4. Marks as processed on success
5. Tracks error with retry info on failure

## Configuration Options

### AetherOutboxOptions

```csharp
new AetherOutboxOptions
{
    ProcessingInterval = TimeSpan.FromSeconds(30),  // Check interval
    MaxRetryCount = 5,                              // Max retries
    RetentionPeriod = TimeSpan.FromDays(7),         // Keep processed
    BatchSize = 100,                                // Messages per batch
    RetryBaseDelay = TimeSpan.FromMinutes(1)        // Exponential backoff base
}
```

### AetherInboxOptions

```csharp
new AetherInboxOptions
{
    RetentionPeriod = TimeSpan.FromDays(7),
    CleanupInterval = TimeSpan.FromHours(1),
    MaxRetryCount = 5,
    RetryBaseDelay = TimeSpan.FromMinutes(1)
}
```

## Entity Models

### OutboxMessage

```csharp
public class OutboxMessage : Entity<Guid>
{
    public string EventName { get; }
    public byte[] EventData { get; }           // Serialized CloudEventEnvelope
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}
```

### InboxMessage

```csharp
public class InboxMessage : Entity<string>     // Id = EventId
{
    public string EventName { get; }
    public byte[] EventData { get; }
    public IncomingEventStatus Status { get; } // Pending, Processed, Discarded
    public DateTime? HandledTime { get; set; }
    public int RetryCount { get; set; }
}
```

## Best Practices

1. **Always use outbox for critical events** - Guarantees delivery even if broker is down
2. **Let EventsController handle idempotency** - Don't implement manual checks
3. **Monitor failed messages** - Set up alerts for messages exceeding MaxRetryCount
4. **Configure appropriate retention** - Balance storage vs debugging needs
5. **Use transactional outbox** - Wrap event publishing in `[UnitOfWork]`

## Related Features

- [Distributed Events](../distributed-events/README.md) - Event bus integration
- [Unit of Work](../unit-of-work/README.md) - Transaction management
- [Domain Events](../domain-events/README.md) - Domain event dispatching
