# Domain Events

## Overview

Domain events enable communication between aggregates and trigger side effects after successful persistence. Events are collected in aggregates and dispatched after UoW commit, with optional outbox support for reliable delivery.

## Quick Start

### Define Event

```csharp
[EventName("order.placed", version: 1)]
public class OrderPlacedEvent : IDistributedEvent
{
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
}
```

### Raise Event from Aggregate

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        if (!Items.Any())
            throw new InvalidOperationException("Order must have items");
        
        Status = OrderStatus.Placed;
        
        // Event dispatched after successful commit
        AddDistributedEvent(new OrderPlacedEvent
        {
            OrderId = Id,
            CustomerId = CustomerId,
            TotalAmount = TotalAmount.Amount
        });
    }
}
```

### Service Usage

```csharp
[UnitOfWork]
public async Task PlaceOrderAsync(Guid orderId)
{
    var order = await _repository.GetAsync(orderId);
    order.PlaceOrder(); // Adds event
    await _repository.UpdateAsync(order);
    
    // On commit:
    // 1. Order saved to database
    // 2. Events dispatched via event bus
}
```

## Configuration

### Service Registration

```csharp
services.AddAetherDomainEventDispatching<MyDbContext>(options =>
{
    options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
});
```

### Dispatch Strategies

```csharp
// Direct publish (no outbox)
options.DispatchStrategy = DomainEventDispatchStrategy.Direct;

// Always use outbox (recommended for reliability)
options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
```

## Event Flow

```
Aggregate.AddDistributedEvent()
    ↓
UoW.CommitAsync()
    ↓
DbContext.SaveChangesAsync()
    ↓
CollectDomainEventsAsync()
    ↓
DomainEventDispatcher.DispatchEventsAsync()
    ↓
EventBus.PublishAsync() or OutboxStore.StoreAsync()
```

## Event Naming

```csharp
// With attribute (recommended)
[EventName("order.placed", version: 1)]
public class OrderPlacedEvent : IDistributedEvent { }

// Topic: order.placed/v1
```

## Best Practices

1. **Raise from aggregates** - Events should originate from domain logic, not services
2. **Use past tense** - OrderPlacedEvent, not PlaceOrderEvent
3. **Include necessary data** - Handlers should not need to query for additional data
4. **Use outbox for reliability** - Prevents event loss if broker is unavailable
5. **Version events** - Use EventNameAttribute for versioning

## Related Features

- [Distributed Events](../distributed-events/README.md) - Event bus and handlers
- [Inbox & Outbox](../inbox-outbox/README.md) - Reliable delivery
- [Unit of Work](../unit-of-work/README.md) - Transaction and dispatch coordination
