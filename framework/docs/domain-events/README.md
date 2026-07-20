# Domain Events

## Overview

Domain events enable communication between aggregates while keeping delivery coordinated with
the owning Unit of Work. Events are collected from aggregate changes, buffered with their
producing schema, and dispatched only from `CommitAsync`.

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
    
    // On commit the order is persisted and the buffered event follows
    // the configured outbox/direct-publish strategy.
}
```

## Configuration

### Service Registration

```csharp
services.AddAetherDomainEvents<MyDbContext>(options =>
{
    options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
});
```

### Dispatch Strategies

```csharp
// Write to outbox as part of the commit pipeline (recommended)
options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;

// Commit business data, publish directly, then use a RequiresNew outbox fallback on failure
options.DispatchStrategy = DomainEventDispatchStrategy.PublishWithFallback;
```

## Event Flow

```
Aggregate.AddDistributedEvent()
    ↓
UoW.SaveChangesAsync() or UoW.CommitAsync()
    ↓
Persist business changes + collect events into schema-bound UoW buffer
    ↓
UoW.CommitAsync()
    ↓
Schema-grouped OutboxStore.StoreAsync() or EventBus.PublishAsync()
```

Domain events are collected during `SaveChanges` and enqueued onto the owning UnitOfWork's
buffer. A `DbContext` must be obtained through the UnitOfWork for its events to be captured.
The buffer records the schema bound to that context, so changing the ambient schema before
commit cannot redirect the event. Events from multiple schemas retain their production order
and are dispatched under their respective schema scopes.

`SaveChangesAsync` never publishes buffered events and never writes them to the outbox merely
because it was called. This is true for both transactional and non-transactional UoWs:

```text
Non-transactional SaveChanges -> business write plus schema-bound event buffer
Non-transactional Commit      -> schema-grouped outbox or direct dispatch
```

With a transactional `AlwaysUseOutbox` UoW, business and outbox rows share the root transaction.
With a non-transactional UoW, each business/outbox save may auto-commit. A crash can therefore
occur between the business write and outbox write: commit-boundary dispatch and error
propagation are guaranteed, but atomicity is not. Consumers must be idempotent and operations
that require atomic business/outbox persistence must use a transaction.

Dispatcher/outbox failures propagate from `CommitAsync`; events are not silently discarded and
the UoW is not marked completed. For `PublishWithFallback`, direct-publish failures are written
to the producing schema's outbox in a new `RequiresNew` scope.

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
