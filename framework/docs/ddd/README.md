# Domain-Driven Design Building Blocks

## Overview

Complete DDD tactical patterns for building rich domain models. Includes Entities, Value Objects, Aggregate Roots with domain events, auditing support, soft delete, and concurrency control.

## Entity Hierarchy

```
Entity<TKey>
  └── CreationAuditedEntity<TKey>
        └── AuditedEntity<TKey>
              └── FullAuditedEntity<TKey>

BasicAggregateRoot<TKey> (with domain events)
  └── AggregateRoot<TKey>
        └── AuditedAggregateRoot<TKey>
              └── FullAuditedAggregateRoot<TKey>
```

## Quick Start

### Simple Entity

```csharp
public class Category : Entity<Guid>
{
    public string Name { get; private set; }
    
    protected Category() { } // For EF Core
    
    public Category(string name)
    {
        Name = name;
    }
    
    public void UpdateName(string newName)
    {
        Name = newName ?? throw new ArgumentNullException(nameof(newName));
    }
}
```

### Value Object

```csharp
public class Money : ValueObject
{
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    
    protected Money() { }
    
    public Money(decimal amount, string currency)
    {
        Amount = amount >= 0 ? amount : throw new ArgumentException("Cannot be negative");
        Currency = currency ?? throw new ArgumentNullException(nameof(currency));
    }
    
    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Amount;
        yield return Currency;
    }
    
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException("Currency mismatch");
        return new Money(Amount + other.Amount, Currency);
    }
}
```

### Aggregate Root with Domain Events

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    private readonly List<OrderItem> _items = new();
    
    public string OrderNumber { get; private set; }
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Money TotalAmount { get; private set; }
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    
    protected Order() { }
    
    public Order(Guid customerId, string orderNumber)
    {
        CustomerId = customerId;
        OrderNumber = orderNumber;
        Status = OrderStatus.Draft;
        TotalAmount = new Money(0, "USD");
    }
    
    public void AddItem(Guid productId, int quantity, Money unitPrice)
    {
        if (Status != OrderStatus.Draft)
            throw new InvalidOperationException("Cannot modify non-draft order");
        
        _items.Add(new OrderItem(Id, productId, quantity, unitPrice));
        RecalculateTotal();
    }
    
    public void PlaceOrder()
    {
        if (!_items.Any())
            throw new InvalidOperationException("Order must have items");
        
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id, CustomerId, TotalAmount));
    }
    
    private void RecalculateTotal()
    {
        TotalAmount = new Money(_items.Sum(i => i.TotalPrice.Amount), "USD");
    }
}
```

## Auditing

### Audited Entity Types

| Type | Properties |
|------|-----------|
| `CreationAuditedEntity` | CreatedAt, CreatedBy |
| `AuditedEntity` | + ModifiedAt, ModifiedBy |
| `FullAuditedEntity` | + IsDeleted, DeletedAt, DeletedBy |

### Automatic Auditing

```csharp
// Automatic with AddAetherDbContext
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(connectionString));

// AuditInterceptor automatically:
// - Sets CreatedAt/CreatedBy on insert
// - Sets ModifiedAt/ModifiedBy on update
// - Sets DeletedAt/DeletedBy on soft delete
```

### Current User Setup

```csharp
services.AddAetherAspNetCore();
app.UseCurrentUser();
```

## Domain Events

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        
        // Event dispatched after successful commit
        AddDistributedEvent(new OrderPlacedEvent(Id, CustomerId));
    }
    
    public void Cancel()
    {
        Status = OrderStatus.Cancelled;
        AddDistributedEvent(new OrderCancelledEvent(Id));
    }
}
```

## EF Core Configuration

```csharp
public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        
        // Value Object
        builder.OwnsOne(p => p.Price, price =>
        {
            price.Property(m => m.Amount).HasColumnName("Price_Amount");
            price.Property(m => m.Currency).HasColumnName("Price_Currency");
        });
        
        // Soft delete filter
        builder.HasQueryFilter(p => !p.IsDeleted);
        
        // Concurrency
        builder.Property(p => p.ConcurrencyStamp).IsConcurrencyToken();
    }
}
```

## Concurrency Control

```csharp
public class Product : AuditedEntity<Guid>, IHasConcurrencyStamp
{
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
}

// AuditInterceptor updates ConcurrencyStamp on save
// EF Core throws DbUpdateConcurrencyException on conflict
```

## Best Practices

1. **Encapsulate business logic** - Put validation and rules in entities, not services
2. **Use private setters** - Protect invariants with methods
3. **Raise events from aggregates** - Use `AddDistributedEvent()` for cross-boundary communication
4. **Use Value Objects** - For concepts without identity (Money, Address, DateRange)
5. **Choose appropriate auditing** - Simple entities don't need full auditing

## Entity Examples by Use Case

```csharp
// Simple reference data - no auditing
public class Category : Entity<Guid> { }

// Content with tracking
public class BlogPost : AuditedEntity<Guid> { }

// Soft-deletable with full audit
public class Customer : FullAuditedAggregateRoot<Guid> { }

// Aggregate with domain events
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}
```

## Related Features

- [Repository Pattern](../repository-pattern/README.md) - Data access for entities
- [Domain Events](../domain-events/README.md) - Event handling from aggregates
- [Unit of Work](../unit-of-work/README.md) - Transaction management
