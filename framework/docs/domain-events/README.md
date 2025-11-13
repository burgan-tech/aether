# Domain Events

## Overview

Domain Events in Aether enable aggregates to communicate state changes to other parts of the system without tight coupling. Events are collected during business operations and automatically dispatched after successful transaction commit, ensuring consistency and reliability.

## Key Features

- **Aggregate-Based** - Events originate from aggregate roots
- **Automatic Dispatching** - Events dispatched after successful commit
- **Dispatch Strategies** - AlwaysUseOutbox or PublishWithFallback
- **Transaction Integration** - Seamless UnitOfWork integration
- **Metadata Extraction** - Automatic event naming and versioning

## Core Interfaces

### IDistributedEvent

Marker interface for events.

```csharp
public interface IDistributedEvent
{
}
```

### IHasDomainEvents

Interface for entities that raise domain events.

```csharp
public interface IHasDomainEvents
{
    IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents();
    void ClearDomainEvents();
}
```

### IDomainEventDispatcher

Service for dispatching domain events.

```csharp
public interface IDomainEventDispatcher
{
    Task DispatchEventsAsync(
        IEnumerable<DomainEventEnvelope> eventEnvelopes, 
        CancellationToken cancellationToken = default);
    
    Task PublishDirectlyAsync(
        IEnumerable<DomainEventEnvelope> eventEnvelopes, 
        CancellationToken cancellationToken = default);
    
    Task WriteToOutboxInNewScopeAsync(
        IEnumerable<DomainEventEnvelope> eventEnvelopes, 
        CancellationToken cancellationToken = default);
}
```

### DomainEventEnvelope

Wrapper for events with metadata.

```csharp
public class DomainEventEnvelope
{
    public IDistributedEvent Event { get; }
    public EventMetadata Metadata { get; }
    
    public DomainEventEnvelope(IDistributedEvent @event, EventMetadata metadata)
    {
        Event = @event;
        Metadata = metadata;
    }
}
```

## Configuration

### Service Registration

```csharp
// Configure domain event dispatching
services.AddAetherDomainEventDispatching<MyDbContext>(options =>
{
    // Strategy: AlwaysUseOutbox (default) or PublishWithFallback
    options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
});

// Requires event bus and outbox
services.AddAetherEventBus(options => { /* ... */ });
services.AddAetherOutbox<MyDbContext>();
```

### Dispatch Strategies

**AlwaysUseOutbox (Recommended)**
- Events written to outbox within transaction
- Guaranteed delivery via background processor
- Best for reliability

**PublishWithFallback**
- Attempts direct publish after commit
- Falls back to outbox on failure
- Lower latency when successful

```csharp
services.AddAetherDomainEventDispatching<MyDbContext>(options =>
{
    options.DispatchStrategy = DomainEventDispatchStrategy.PublishWithFallback;
});
```

## Usage Examples

### Defining Events

```csharp
[EventName("order.placed", "v1", PubSubName = "pubsub")]
public class OrderPlacedEvent : IDistributedEvent
{
    [EventSubject]
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime PlacedAt { get; set; }
    
    public OrderPlacedEvent(Guid orderId, Guid customerId, decimal totalAmount)
    {
        OrderId = orderId;
        CustomerId = customerId;
        TotalAmount = totalAmount;
        PlacedAt = DateTime.UtcNow;
    }
}
```

### Raising Events in Aggregates

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public OrderStatus Status { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal TotalAmount { get; private set; }
    
    public void PlaceOrder()
    {
        if (Status != OrderStatus.Draft)
            throw new InvalidOperationException("Order already placed");
        
        Status = OrderStatus.Placed;
        
        // Add domain event
        AddDistributedEvent(new OrderPlacedEvent(Id, CustomerId, TotalAmount));
    }
    
    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Order already cancelled");
        
        Status = OrderStatus.Cancelled;
        AddDistributedEvent(new OrderCancelledEvent(Id));
    }
}
```

### Events Dispatched Automatically

```csharp
[UnitOfWork]
public async Task PlaceOrderAsync(Guid orderId)
{
    var order = await _orderRepository.GetAsync(orderId);
    order.PlaceOrder(); // Adds event to aggregate
    
    await _orderRepository.UpdateAsync(order);
    
    // On commit:
    // 1. Order is saved to database
    // 2. Events are collected from aggregate
    // 3. Events are dispatched (or written to outbox)
    // 4. Aggregate events are cleared
}
```

### Multiple Events

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void CompleteOrder()
    {
        Status = OrderStatus.Completed;
        
        // Multiple events for different bounded contexts
        AddDistributedEvent(new OrderCompletedEvent(Id));
        AddDistributedEvent(new CustomerLoyaltyPointsEarnedEvent(CustomerId, CalculatePoints()));
        AddDistributedEvent(new InventoryConfirmedEvent(Id));
        
        // All dispatched together after commit
    }
}
```

## Best Practices

### 1. Name Events in Past Tense

```csharp
// ✅ Good
public class OrderPlacedEvent { }
public class PaymentProcessedEvent { }

// ❌ Bad
public class PlaceOrderEvent { }
public class ProcessPaymentEvent { }
```

### 2. Include All Necessary Data

```csharp
// ✅ Good: Self-contained event
public class OrderPlacedEvent
{
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public List<OrderItemDto> Items { get; set; }
}

// ❌ Bad: Requires additional queries
public class OrderPlacedEvent
{
    public Guid OrderId { get; set; }
    // Missing critical data
}
```

### 3. Raise Events from Aggregates Only

```csharp
// ✅ Good: Event raised from aggregate
public class Order : AggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}

// ❌ Bad: Event raised from service
public class OrderService
{
    public async Task PlaceOrderAsync(Guid orderId)
    {
        var order = await _repository.GetAsync(orderId);
        order.Status = OrderStatus.Placed;
        await _repository.UpdateAsync(order);
        await _eventBus.PublishAsync(new OrderPlacedEvent(orderId)); // Wrong!
    }
}
```

### 4. Use EventNameAttribute for Versioning

```csharp
// Version 1
[EventName("product.price.changed", "v1")]
public class ProductPriceChangedEvent { }

// Version 2 with breaking changes
[EventName("product.price.changed", "v2")]
public class ProductPriceChangedEventV2 { }
```

## Testing

```csharp
public class OrderTests
{
    [Fact]
    public void PlaceOrder_ShouldRaiseOrderPlacedEvent()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), "ORD-001");
        order.AddItem(Guid.NewGuid(), 2, 10.99m);
        
        // Act
        order.PlaceOrder();
        
        // Assert
        var events = order.GetDomainEvents();
        Assert.Single(events);
        
        var orderPlacedEvent = events.First().Event as OrderPlacedEvent;
        Assert.NotNull(orderPlacedEvent);
        Assert.Equal(order.Id, orderPlacedEvent.OrderId);
    }
}
```

## Related Features

- **[DDD Building Blocks](../ddd/README.md)** - Aggregates that raise events
- **[Distributed Events](../distributed-events/README.md)** - Event bus for publishing
- **[Inbox & Outbox](../inbox-outbox/README.md)** - Reliable event delivery
- **[Unit of Work](../unit-of-work/README.md)** - Transaction integration

## Common Issues

### Issue: Events not dispatching

**Solution:** Ensure domain event dispatching is configured:

```csharp
services.AddAetherDomainEventDispatching<MyDbContext>();
```

### Issue: Events dispatched multiple times

**Cause:** Using `PublishAsync` directly instead of `AddDistributedEvent`.

**Solution:** Use `AddDistributedEvent` in aggregates only.

