# Distributed Events & Event Bus

## Overview

Aether's Event Bus provides a CloudEvents-based abstraction for publishing and subscribing to distributed events across microservices. It integrates seamlessly with Dapr Pub/Sub and supports automatic handler discovery, topic naming strategies, and reliable delivery patterns.

## Key Features

- **CloudEvents Standard** - Industry-standard event envelope format
- **Dapr Integration** - Built-in Dapr Pub/Sub support
- **Automatic Handler Discovery** - Convention-based handler registration
- **Topic Naming Strategies** - Environment-based prefixing
- **Inbox/Outbox Support** - Reliable messaging patterns
- **Attribute-Based Configuration** - EventNameAttribute for metadata

## Core Interfaces

### IDistributedEventBus

Main interface for publishing events.

```csharp
public interface IDistributedEventBus : IEventBus
{
    Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true, 
        CancellationToken cancellationToken = default) where TEvent : class;
    
    Task PublishAsync(
        IDistributedEvent @event,
        EventMetadata metadata,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default);
}
```

### IDistributedEventHandler<TEvent>

Interface for event handlers.

```csharp
public interface IDistributedEventHandler<in TEvent> where TEvent : class
{
    Task HandleEventAsync(TEvent @event, CancellationToken cancellationToken = default);
}
```

## Configuration

### Service Registration

```csharp
services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "my-service";
    
    // Register handlers
    options.RegisterHandlers(typeof(Program).Assembly);
});
```

### Event Subscription Endpoint

```csharp
app.MapAetherEventSubscriptions(); // Maps /api/aether-events/subscribe endpoint
```

## Event Definition

### EventNameAttribute

Define event metadata with attributes.

```csharp
[EventName("order.placed", "v1", PubSubName = "pubsub")]
public class OrderPlacedEvent : IDistributedEvent
{
    [EventSubject]
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
}
```

### CloudEventEnvelope

Events are wrapped in CloudEvents format.

```csharp
public class CloudEventEnvelope
{
    public string Id { get; set; } // Auto-generated GUID
    public string Type { get; set; } // From EventNameAttribute
    public string Source { get; set; } // From options
    public string? Subject { get; set; } // From EventSubjectAttribute
    public DateTime Time { get; set; } // UTC Now
    public string SpecVersion { get; set; } = "1.0";
    public string DataContentType { get; set; } = "application/json";
    public string? DataSchema { get; set; }
    public object Data { get; set; } // The actual event
}
```

## Publishing Events

### Direct Publishing

```csharp
public class OrderService
{
    private readonly IDistributedEventBus _eventBus;
    
    public async Task PlaceOrderAsync(PlaceOrderDto dto)
    {
        var order = CreateOrder(dto);
        await _orderRepository.InsertAsync(order);
        
        // Publish with outbox (default)
        await _eventBus.PublishAsync(new OrderPlacedEvent
        {
            OrderId = order.Id,
            CustomerId = order.CustomerId,
            TotalAmount = order.TotalAmount
        });
    }
}
```

### Publishing Without Outbox

```csharp
// Direct publish (no transactional guarantee)
await _eventBus.PublishAsync(
    new OrderPlacedEvent(orderId), 
    useOutbox: false);
```

### Custom Subject

```csharp
await _eventBus.PublishAsync(
    new OrderPlacedEvent(orderId),
    subject: $"order-{orderId}");
```

## Event Handlers

### Implementing Handlers

```csharp
public class OrderPlacedEventHandler : IDistributedEventHandler<OrderPlacedEvent>
{
    private readonly ILogger<OrderPlacedEventHandler> _logger;
    private readonly IEmailService _emailService;
    
    public OrderPlacedEventHandler(
        ILogger<OrderPlacedEventHandler> logger,
        IEmailService emailService)
    {
        _logger = logger;
        _emailService = emailService;
    }
    
    public async Task HandleEventAsync(
        OrderPlacedEvent @event, 
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing order placed event for order {OrderId}", @event.OrderId);
        
        await _emailService.SendOrderConfirmationAsync(@event.OrderId, cancellationToken);
        
        _logger.LogInformation("Order confirmation sent for order {OrderId}", @event.OrderId);
    }
}
```

### Handler with Inbox (Idempotency)

```csharp
public class PaymentProcessedEventHandler : IDistributedEventHandler<PaymentProcessedEvent>
{
    private readonly IInboxStore _inboxStore;
    
    public async Task HandleEventAsync(
        PaymentProcessedEvent @event, 
        CancellationToken cancellationToken)
    {
        var messageId = /* extract from context */;
        
        // Check inbox for idempotency
        if (await _inboxStore.ExistsAsync(messageId, cancellationToken))
        {
            return; // Already processed
        }
        
        // Process event
        await ProcessPaymentAsync(@event);
        
        // Mark as processed
        await _inboxStore.MarkAsProcessedAsync(messageId, cancellationToken);
    }
}
```

## Topic Naming

### Default Naming Strategy

```csharp
// EventName: "order.placed", Version: "v1"
// Topic: "order.placed.v1"

// With environment (Production):
// Topic: "production.order.placed.v1"
```

### Custom Topic Strategy

```csharp
public class CustomTopicNameStrategy : ITopicNameStrategy
{
    public string GetTopicName(Type eventType)
    {
        var eventInfo = EventNameAttribute.GetEventNameInfo(eventType);
        return $"custom.{eventInfo.EventName}.{eventInfo.Version}";
    }
}

services.AddSingleton<ITopicNameStrategy, CustomTopicNameStrategy>();
```

## Dapr Integration

### Dapr Configuration

```yaml
# pubsub.yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: pubsub
spec:
  type: pubsub.redis
  version: v1
  metadata:
  - name: redisHost
    value: localhost:6379
  - name: redisPassword
    value: ""
```

### Subscription Configuration

Aether automatically generates Dapr subscriptions:

```json
[
  {
    "pubsubname": "pubsub",
    "topic": "order.placed.v1",
    "route": "/api/aether-events/order.placed.v1"
  }
]
```

## Custom Controller Implementation

Aether provides abstract base classes for event receiving and discovery endpoints, allowing you to implement them with custom routes in your own project. This plug-and-play architecture gives you full control over endpoint configuration while leveraging the framework's event processing logic.

### EventsController

The `EventsController` is an abstract base class that handles incoming events from Dapr. You can inherit from it and create your own controller with custom routes.

#### Abstract Base Class

The base class provides:
- `ProcessEventAsync()` - Main event processing method
- `ReadRequestBodyAsync()` - Request body reading (virtual, can be overridden)
- `TryExtractEventId()` - CloudEvent ID extraction (virtual, can be overridden)
- `TryMarkEventAsProcessedAsync()` - Inbox marking (virtual, can be overridden)
- Protected access to all dependencies (InvokerRegistry, InboxStore, etc.)

#### Example Implementation

```csharp
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("api/events/{name}/v{version:int}")]
public sealed class MyEventsController(
    IDistributedEventInvokerRegistry invokerRegistry,
    IInboxStore inboxStore,
    IUnitOfWorkManager unitOfWorkManager,
    IServiceProvider serviceProvider,
    IEventSerializer serializer,
    ILogger<MyEventsController> logger) 
    : EventsController(invokerRegistry, inboxStore, unitOfWorkManager, serviceProvider, serializer, logger)
{
    /// <summary>
    /// Handles incoming events from Dapr.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post(
        [FromRoute] string name,
        [FromRoute] int version,
        CancellationToken cancellationToken)
    {
        return await ProcessEventAsync(name, version, cancellationToken);
    }
}
```

#### Customizing Behavior

You can override virtual methods to customize event processing:

```csharp
[ApiController]
[Route("api/events/{name}/v{version:int}")]
public sealed class CustomEventsController : EventsController
{
    // Override to add custom logging or validation
    protected override async Task<IActionResult> ProcessEventAsync(
        string name, 
        int version, 
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Custom pre-processing for {Name} v{Version}", name, version);
        
        var result = await base.ProcessEventAsync(name, version, cancellationToken);
        
        Logger.LogInformation("Custom post-processing completed");
        return result;
    }
    
    // Override to customize event ID extraction
    protected override string? TryExtractEventId(byte[] payload, string name, int version)
    {
        // Custom logic for extracting event ID
        return base.TryExtractEventId(payload, name, version);
    }
    
    // Override to customize request body reading
    protected override async Task<byte[]> ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        // Custom body reading logic (e.g., size limits, validation)
        return await base.ReadRequestBodyAsync(cancellationToken);
    }
}
```

### DaprDiscoveryController

The `DaprDiscoveryController` is an abstract base class for the Dapr subscription discovery endpoint. Dapr calls this endpoint to discover which events your service subscribes to.

#### Abstract Base Class

The base class provides:
- `GetSubscriptions()` - Returns subscription configuration (virtual, can be overridden)
- Protected access to `InvokerRegistry`
- Protected `JsonOptions` for serialization

#### Example Implementation

```csharp
using BBT.Aether.Events;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/dapr")]
public sealed class MyDaprDiscoveryController(IDistributedEventInvokerRegistry invokerRegistry) 
    : DaprDiscoveryController(invokerRegistry)
{
    /// <summary>
    /// Returns Dapr subscription configuration.
    /// Dapr runtime calls this endpoint to discover subscriptions.
    /// </summary>
    [HttpGet("subscribe", Order = int.MinValue)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
    {
        return GetSubscriptions();
    }
}
```

#### Customizing Subscriptions

You can override `GetSubscriptions()` to customize the subscription response:

```csharp
[ApiController]
[Route("api/dapr")]
public sealed class CustomDaprDiscoveryController : DaprDiscoveryController
{
    [HttpGet("subscribe")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
    {
        return GetSubscriptions();
    }
    
    protected override IActionResult GetSubscriptions()
    {
        // Add custom logic before returning subscriptions
        Logger.LogInformation("Dapr requesting subscriptions");
        
        var result = base.GetSubscriptions();
        
        // Add custom logic after generating subscriptions
        return result;
    }
    
    // Alternative: Create multiple endpoints
    [HttpGet("subscriptions")]
    public IActionResult AlternativeRoute()
    {
        return GetSubscriptions();
    }
}
```

#### Important Notes

- **Dapr Default Route**: Dapr expects the discovery endpoint at `/dapr/subscribe` by default, but you can configure a custom route in your Dapr configuration.
- **Route Matching**: Ensure your event routes in `EventsController` match the routes returned by `GetSubscriptions()`.
- **Order Attribute**: Use `Order = int.MinValue` to ensure this endpoint is matched before other routes.

### Complete Setup Example

```csharp
// Program.cs or Startup.cs
services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "my-service";
    options.RegisterHandlers(typeof(Program).Assembly);
});

// Controllers are automatically discovered by ASP.NET Core
// No need to manually map endpoints
```

With this approach:
1. Define your event handlers with `[EventName]` attributes
2. Create your concrete `EventsController` with your desired route
3. Create your concrete `DaprDiscoveryController` with your desired route
4. Dapr will automatically discover and subscribe to your events

## Usage Examples

### Complete Example

```csharp
// 1. Define Event
[EventName("inventory.reserved", "v1")]
public class InventoryReservedEvent : IDistributedEvent
{
    [EventSubject]
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public Guid ReservationId { get; set; }
}

// 2. Implement Handler
public class InventoryReservedEventHandler 
    : IDistributedEventHandler<InventoryReservedEvent>
{
    private readonly IRepository<Product> _productRepository;
    
    [UnitOfWork]
    public async Task HandleEventAsync(
        InventoryReservedEvent @event, 
        CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetAsync(@event.ProductId);
        product.DecreaseStock(@event.Quantity);
        await _productRepository.UpdateAsync(product);
    }
}

// 3. Publish Event
public class OrderService
{
    [UnitOfWork]
    public async Task CreateOrderAsync(CreateOrderDto dto)
    {
        var order = new Order(dto);
        await _orderRepository.InsertAsync(order);
        
        // Publish with outbox
        await _eventBus.PublishAsync(new InventoryReservedEvent
        {
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            ReservationId = Guid.NewGuid()
        });
    }
}
```

### Saga Pattern with Events

```csharp
// Order Service publishes
await _eventBus.PublishAsync(new OrderCreatedEvent(orderId));

// Payment Service subscribes and publishes
public class OrderCreatedEventHandler : IDistributedEventHandler<OrderCreatedEvent>
{
    public async Task HandleEventAsync(OrderCreatedEvent @event, CancellationToken ct)
    {
        var payment = await ProcessPaymentAsync(@event.OrderId);
        await _eventBus.PublishAsync(new PaymentProcessedEvent(payment.Id, @event.OrderId));
    }
}

// Shipping Service subscribes
public class PaymentProcessedEventHandler : IDistributedEventHandler<PaymentProcessedEvent>
{
    public async Task HandleEventAsync(PaymentProcessedEvent @event, CancellationToken ct)
    {
        await CreateShipmentAsync(@event.OrderId);
        await _eventBus.PublishAsync(new ShipmentCreatedEvent(@event.OrderId));
    }
}
```

## Best Practices

### 1. Use Outbox for Critical Events

```csharp
// ✅ Good: Use outbox for transactional consistency
[UnitOfWork]
public async Task ProcessOrderAsync()
{
    await _repository.InsertAsync(order);
    await _eventBus.PublishAsync(event, useOutbox: true);
}

// ❌ Bad: Direct publish loses transactional guarantee
await _eventBus.PublishAsync(event, useOutbox: false);
```

### 2. Handle Idempotency

```csharp
public class EventHandler : IDistributedEventHandler<MyEvent>
{
    public async Task HandleEventAsync(MyEvent @event, CancellationToken ct)
    {
        // Check if already processed
        if (await AlreadyProcessedAsync(@event.Id))
            return;
        
        // Process
        await ProcessAsync(@event);
        
        // Mark as processed
        await MarkProcessedAsync(@event.Id);
    }
}
```

### 3. Version Your Events

```csharp
// Version 1
[EventName("order.created", "v1")]
public class OrderCreatedEventV1 { }

// Version 2 - new field
[EventName("order.created", "v2")]
public class OrderCreatedEventV2 { }

// Support both versions during migration
```

## Testing

```csharp
public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrder_ShouldPublishEvent()
    {
        // Arrange
        var mockEventBus = new Mock<IDistributedEventBus>();
        var service = new OrderService(mockEventBus.Object, ...);
        
        // Act
        await service.CreateOrderAsync(dto);
        
        // Assert
        mockEventBus.Verify(
            e => e.PublishAsync(
                It.IsAny<OrderCreatedEvent>(), 
                null, 
                true, 
                default), 
            Times.Once);
    }
}
```

## Related Features

- **[Domain Events](../domain-events/README.md)** - Events from aggregates
- **[Inbox & Outbox](../inbox-outbox/README.md)** - Reliable messaging
- **[Unit of Work](../unit-of-work/README.md)** - Transaction integration

