# Distributed Events & Event Bus

## Overview

CloudEvents-based event bus with Dapr integration. Provides automatic handler discovery, topic naming strategies, and seamless integration with domain events and outbox pattern.

## Quick Start

### Service Registration

```csharp
services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "myapp";
    options.TopicPrefix = "dev"; // Environment-based prefix
});
```

### Define Event

```csharp
[EventName("order.created", version: 1)]
public class OrderCreatedEvent : IDistributedEvent
{
    public Guid OrderId { get; set; }
    public string CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
}
```

### Publish Event

```csharp
public class OrderService
{
    private readonly IDistributedEventBus _eventBus;
    
    [UnitOfWork]
    public async Task CreateOrderAsync(CreateOrderDto dto)
    {
        var order = new Order(dto);
        await _repository.InsertAsync(order);
        
        await _eventBus.PublishAsync(new OrderCreatedEvent
        {
            OrderId = order.Id,
            CustomerName = dto.CustomerName,
            TotalAmount = order.TotalAmount
        });
    }
}
```

### Handle Event

```csharp
public class OrderCreatedEventHandler : IDistributedEventHandler<OrderCreatedEvent>
{
    public async Task HandleAsync(OrderCreatedEvent @event, CancellationToken ct)
    {
        // Process event
        await _emailService.SendOrderConfirmationAsync(@event.OrderId, ct);
    }
}
```

## Event Controller Setup

### Using EventsController

```csharp
[ApiController]
[Route("api/events")]
public class MyEventsController : EventsController
{
    public MyEventsController(/* dependencies */) : base(/* dependencies */) { }

    [HttpPost("{name}/v{version}")]
    public async Task<IActionResult> HandleEvent(string name, int version, CancellationToken ct)
    {
        return await ProcessEventAsync(name, version, ct);
    }
}
```

### Dapr Subscription Discovery

```csharp
[ApiController]
[Route("dapr")]
public class DaprController : DaprDiscoveryController
{
    public DaprController(/* dependencies */) : base(/* dependencies */) { }

    [HttpGet("subscribe")]
    public IActionResult GetSubscriptions()
    {
        return BuildSubscriptions(pubsubName: "pubsub", route: "/api/events");
    }
}
```

## Handler Registration

```csharp
// Automatic discovery
services.AddAetherEventBus(options =>
{
    options.AutoDiscoverHandlers = true;
    options.HandlerAssemblies = new[] { typeof(Program).Assembly };
});

// Manual registration
services.AddAetherEventBus(options =>
{
    options.RegisterHandler<OrderCreatedEventHandler>();
});
```

## Event Naming

```csharp
[EventName("order.created", version: 1)]
public class OrderCreatedEvent : IDistributedEvent { }

// Topic: {prefix}.order.created/v1
// Example: dev.order.created/v1
```

## Configuration Options

```csharp
services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";           // Dapr pubsub component
    options.DefaultSource = "order-service"; // CloudEvent source
    options.TopicPrefix = "dev";             // Environment prefix
    options.AutoDiscoverHandlers = true;     // Auto-register handlers
});
```

## Tracing

All event bus operations are automatically instrumented with OpenTelemetry spans via the `BBT.Aether.Infrastructure` ActivitySource. No additional configuration is required beyond enabling Aether telemetry.

### Publishing Spans

| Span | Kind | Description |
|------|------|-------------|
| `EventBus.Publish` | Producer | Created when `PublishAsync` is called (both generic and metadata-based overloads) |
| `EventBus.PublishEnvelope` | Producer | Created when a pre-serialized envelope is published (used by outbox processor) |
| `EventBus.PublishToBroker` | Producer | Created when the event is sent to the Dapr PubSub broker |

### Processing Spans

| Span | Kind | Description |
|------|------|-------------|
| `Outbox.Process` | Producer | Created per message in `OutboxProcessor` when republishing from the outbox |
| `Inbox.Process` | Consumer | Created per message in `InboxProcessor` when processing a pending inbox event |
| `Inbox.Invoke` | Internal | Created in `DistributedEventInvoker` when deserializing and calling the event handler |

### Semantic Tags

| Tag | Example | Description |
|-----|---------|-------------|
| `event.name` | `"order.created"` | CloudEvent Type / event name |
| `event.topic` | `"dev.order.created/v1"` | Broker topic |
| `event.pubsub_name` | `"pubsub"` | Dapr PubSub component |
| `event.broker` | `"dapr"` | Broker implementation |
| `event.use_outbox` | `true` / `false` | Whether outbox pattern was used |
| `event.id` | `"abc123"` | CloudEvent ID (inbox) |
| `event.version` | `1` | Event version |
| `event.handler` | `"OrderCreatedEventHandler"` | Handler type name |
| `outbox.message_id` | GUID | Outbox message entity ID |
| `outbox.retry_count` | `0` | Current retry attempt |

### Example Trace Hierarchy

**Direct publish:**
```
[ASP.NET Core] POST /api/orders
  └─ [BBT.Aether.Infrastructure] EventBus.Publish
       └─ [BBT.Aether.Infrastructure] EventBus.PublishToBroker
            └─ [HTTP Client] POST http://localhost:3500/v1.0/publish/pubsub/order.created
```

**Outbox publish:**
```
[ASP.NET Core] POST /api/orders
  └─ [BBT.Aether.Infrastructure] EventBus.Publish  (use_outbox=true)
...later (background)...
[BBT.Aether.Infrastructure] Outbox.Process
  └─ [BBT.Aether.Infrastructure] EventBus.PublishEnvelope
       └─ [BBT.Aether.Infrastructure] EventBus.PublishToBroker
            └─ [HTTP Client] POST http://localhost:3500/...
```

**Inbox consumption:**
```
[ASP.NET Core] POST /events/order.created/v1  (Dapr delivers event)
...later (background)...
[BBT.Aether.Infrastructure] Inbox.Process
  └─ [BBT.Aether.Infrastructure] Inbox.Invoke
       └─ (handler code runs here)
```

## Best Practices

1. **Use EventNameAttribute** - Explicit topic naming with versioning
2. **Make handlers idempotent** - Events may be delivered multiple times
3. **Use inbox for critical handlers** - Prevents duplicate processing
4. **Include correlation ID** - For distributed tracing
5. **Keep events small** - Include only necessary data

## Related Features

- [Domain Events](../domain-events/README.md) - Raising events from aggregates
- [Inbox & Outbox](../inbox-outbox/README.md) - Reliable delivery and idempotency
- [Telemetry](../telemetry/README.md) - Distributed tracing for events
