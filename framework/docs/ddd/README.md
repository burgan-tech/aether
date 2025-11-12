# Domain-Driven Design Building Blocks

## Overview

Aether provides comprehensive support for Domain-Driven Design (DDD) tactical patterns, enabling you to build rich, expressive domain models that encapsulate business logic and maintain consistency boundaries.

The framework includes:
- **Entities** - Objects with unique identity
- **Value Objects** - Immutable objects defined by their attributes
- **Aggregate Roots** - Consistency boundaries with domain events
- **Auditing Support** - Automatic tracking of creation and modification
- **Soft Delete** - Non-destructive entity deletion
- **Concurrency Control** - Optimistic locking with concurrency stamps

## Architecture

### Entity Hierarchy

```
IEntity
  ├── Entity (composite key)
  │     ├── CreationAuditedEntity
  │     │     ├── AuditedEntity
  │     │     │     └── FullAuditedEntity
  │     │     └── CreationAuditedAggregateRoot
  │     │           └── AuditedAggregateRoot
  │     │                 └── FullAuditedAggregateRoot
  │     └── BasicAggregateRoot
  │           └── AggregateRoot
  └── IEntity<TKey> (single key)
        └── Entity<TKey>
```

## Core Interfaces & Classes

### IEntity

Base interface for all entities.

```csharp
public interface IEntity
{
    /// <summary>
    /// Gets the composite keys for this entity.
    /// Used for entities with multiple keys or complex identity.
    /// </summary>
    object?[] GetKeys();
}
```

### IEntity<TKey>

Interface for entities with a single primary key.

```csharp
public interface IEntity<TKey> : IEntity
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    TKey Id { get; }
}
```

### Entity

Base class for entities without a specific key type (composite keys).

```csharp
public abstract class Entity : IEntity
{
    public abstract object?[] GetKeys();
    
    public override string ToString()
    {
        return $"{GetType().Name} Keys = {GetKeys().JoinAsString(", ")}";
    }
}
```

### Entity<TKey>

Base class for entities with a single typed primary key.

```csharp
public abstract class Entity<TKey> : Entity, IEntity<TKey>
{
    public virtual TKey Id { get; protected set; } = default!;
    
    protected Entity()
    {
    }
    
    protected Entity(TKey id)
    {
        Id = id;
    }
    
    public override object?[] GetKeys()
    {
        return new object?[] { Id };
    }
    
    public override string ToString()
    {
        return $"[{GetType().Name}] Id = {Id}";
    }
}
```

## Value Objects

### ValueObject

Base class for value objects that are defined by their attributes.

```csharp
public abstract class ValueObject
{
    /// <summary>
    /// Gets the atomic values that define equality for this value object.
    /// </summary>
    protected abstract IEnumerable<object> GetAtomicValues();
    
    public bool ValueEquals(object obj)
    {
        if (obj == null || obj.GetType() != GetType())
        {
            return false;
        }
        
        var other = (ValueObject)obj;
        var thisValues = GetAtomicValues().GetEnumerator();
        var otherValues = other.GetAtomicValues().GetEnumerator();
        
        while (thisValues.MoveNext() && otherValues.MoveNext())
        {
            if (ReferenceEquals(thisValues.Current, null) ^ 
                ReferenceEquals(otherValues.Current, null))
            {
                return false;
            }
            
            if (thisValues.Current != null && 
                !thisValues.Current.Equals(otherValues.Current))
            {
                return false;
            }
        }
        
        return !thisValues.MoveNext() && !otherValues.MoveNext();
    }
    
    public override int GetHashCode()
    {
        return GetAtomicValues()
            .Select(x => x != null ? x.GetHashCode() : 0)
            .Aggregate((x, y) => x ^ y);
    }
}
```

## Aggregate Roots

### IHasDomainEvents

Interface for entities that can raise domain events.

```csharp
public interface IHasDomainEvents
{
    /// <summary>
    /// Gets all domain events raised by this aggregate.
    /// </summary>
    IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents();
    
    /// <summary>
    /// Clears all domain events.
    /// </summary>
    void ClearDomainEvents();
}
```

### IAggregateRoot

Marker interface for aggregate roots.

```csharp
public interface IAggregateRoot : IEntity
{
}

public interface IAggregateRoot<TKey> : IEntity<TKey>, IAggregateRoot
{
}
```

### BasicAggregateRoot

Basic aggregate root with domain events support.

```csharp
[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot,
    IHasDomainEvents
{
    private readonly List<DomainEventEnvelope> _domainEvents = new();
    
    /// <summary>
    /// Adds a distributed event to be published after the aggregate is persisted.
    /// Events are dispatched after SaveChanges completes successfully.
    /// </summary>
    protected void AddDistributedEvent(IDistributedEvent @event)
    {
        // Extract metadata once at the time of adding the event
        var metadata = EventMetadataExtractor.Extract(@event);
        var envelope = new DomainEventEnvelope(@event, metadata);
        _domainEvents.Add(envelope);
    }
    
    public IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }
    
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}
```

### BasicAggregateRoot<TKey>

Typed-key variant of BasicAggregateRoot.

```csharp
[Serializable]
public abstract class BasicAggregateRoot<TKey> : Entity<TKey>,
    IAggregateRoot<TKey>,
    IHasDomainEvents
{
    private readonly List<DomainEventEnvelope> _domainEvents = new();
    
    protected BasicAggregateRoot()
    {
    }
    
    protected BasicAggregateRoot(TKey id) : base(id)
    {
    }
    
    protected void AddDistributedEvent(IDistributedEvent @event)
    {
        var metadata = EventMetadataExtractor.Extract(@event);
        var envelope = new DomainEventEnvelope(@event, metadata);
        _domainEvents.Add(envelope);
    }
    
    public IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }
    
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}
```

### AggregateRoot & AggregateRoot<TKey>

Simplified aggregate root classes without additional features.

```csharp
public abstract class AggregateRoot : BasicAggregateRoot
{
    protected AggregateRoot()
    {
    }
}

public abstract class AggregateRoot<TKey> : BasicAggregateRoot<TKey>
{
    protected AggregateRoot()
    {
    }
    
    protected AggregateRoot(TKey id) : base(id)
    {
    }
}
```

## Auditing

### IHasCreatedAt & IHasCreatedBy

Interfaces for creation auditing.

```csharp
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}

public interface IHasCreatedBy
{
    string? CreatedBy { get; set; }
    string? CreatedByBehalfOf { get; set; }
}

public interface ICreationAuditedObject : IHasCreatedAt, IHasCreatedBy
{
}
```

### IHasModifiedAt & IHasModifiedBy

Interfaces for modification auditing.

```csharp
public interface IHasModifiedAt
{
    DateTime? ModifiedAt { get; set; }
}

public interface IHasModifiedBy
{
    string? ModifiedBy { get; set; }
    string? ModifiedByBehalfOf { get; set; }
}

public interface IAuditedObject : ICreationAuditedObject, IHasModifiedAt, IHasModifiedBy
{
}
```

### CreationAuditedEntity

Entity with creation auditing.

```csharp
public abstract class CreationAuditedEntity : Entity, ICreationAuditedObject
{
    public virtual DateTime CreatedAt { get; set; }
    public virtual string? CreatedBy { get; set; }
    public virtual string? CreatedByBehalfOf { get; set; }
}

public abstract class CreationAuditedEntity<TKey> : Entity<TKey>, ICreationAuditedObject
{
    public virtual DateTime CreatedAt { get; set; }
    public virtual string? CreatedBy { get; set; }
    public virtual string? CreatedByBehalfOf { get; set; }
}
```

### AuditedEntity

Entity with creation and modification auditing.

```csharp
public abstract class AuditedEntity : CreationAuditedEntity, IAuditedObject
{
    public virtual DateTime? ModifiedAt { get; set; }
    public virtual string? ModifiedBy { get; set; }
    public virtual string? ModifiedByBehalfOf { get; set; }
}

public abstract class AuditedEntity<TKey> : CreationAuditedEntity<TKey>, IAuditedObject
{
    public virtual DateTime? ModifiedAt { get; set; }
    public virtual string? ModifiedBy { get; set; }
    public virtual string? ModifiedByBehalfOf { get; set; }
}
```

### FullAuditedEntity

Entity with creation, modification, and soft delete auditing.

```csharp
public abstract class FullAuditedEntity : AuditedEntity, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual DateTime? DeletedAt { get; set; }
    public virtual string? DeletedBy { get; set; }
    public virtual string? DeletedByBehalfOf { get; set; }
}

public abstract class FullAuditedEntity<TKey> : AuditedEntity<TKey>, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual DateTime? DeletedAt { get; set; }
    public virtual string? DeletedBy { get; set; }
    public virtual string? DeletedByBehalfOf { get; set; }
}
```

### Audited Aggregate Roots

```csharp
public abstract class CreationAuditedAggregateRoot : BasicAggregateRoot, ICreationAuditedObject
{
    public virtual DateTime CreatedAt { get; set; }
    public virtual string? CreatedBy { get; set; }
    public virtual string? CreatedByBehalfOf { get; set; }
}

public abstract class AuditedAggregateRoot<TKey> : BasicAggregateRoot<TKey>, IAuditedObject
{
    public virtual DateTime CreatedAt { get; set; }
    public virtual string? CreatedBy { get; set; }
    public virtual string? CreatedByBehalfOf { get; set; }
    public virtual DateTime? ModifiedAt { get; set; }
    public virtual string? ModifiedBy { get; set; }
    public virtual string? ModifiedByBehalfOf { get; set; }
}

public abstract class FullAuditedAggregateRoot<TKey> : AuditedAggregateRoot<TKey>, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual DateTime? DeletedAt { get; set; }
    public virtual string? DeletedBy { get; set; }
    public virtual string? DeletedByBehalfOf { get; set; }
}
```

## Concurrency Control

### IHasConcurrencyStamp

Interface for optimistic concurrency control.

```csharp
public interface IHasConcurrencyStamp
{
    string ConcurrencyStamp { get; set; }
}
```

The `AuditInterceptor` automatically manages concurrency stamps:
- Sets initial stamp on creation
- Updates stamp on modification
- EF Core uses it for concurrency checks

## Configuration

### Automatic Auditing

Auditing is automatically configured when using `AddAetherDbContext`:

```csharp
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(connectionString));

// This registers the AuditInterceptor which:
// - Sets CreatedAt/CreatedBy on insert
// - Sets ModifiedAt/ModifiedBy on update
// - Sets DeletedAt/DeletedBy on soft delete
// - Generates GUIDs for entities with Guid Id
// - Manages concurrency stamps
```

### Current User Resolution

Configure current user accessor for auditing:

```csharp
services.AddAetherAspNetCore(); // Registers current user middleware

app.UseCurrentUser(); // Adds middleware to resolve current user
```

## Usage Examples

### Simple Entity

```csharp
public class Category : Entity<Guid>
{
    public string Name { get; private set; }
    public string Description { get; private set; }
    
    protected Category() { } // For EF Core
    
    public Category(string name, string description)
    {
        Name = name;
        Description = description;
    }
    
    public void UpdateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Name cannot be empty", nameof(newName));
        
        Name = newName;
    }
}
```

### Value Object Example

```csharp
public class Money : ValueObject
{
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    
    protected Money() { } // For EF Core
    
    public Money(decimal amount, string currency)
    {
        if (amount < 0)
            throw new ArgumentException("Amount cannot be negative");
        
        Amount = amount;
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
            throw new InvalidOperationException("Cannot add money with different currencies");
        
        return new Money(Amount + other.Amount, Currency);
    }
    
    public static bool operator ==(Money? left, Money? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.ValueEquals(right);
    }
    
    public static bool operator !=(Money? left, Money? right) => !(left == right);
    
    public override bool Equals(object? obj) => ValueEquals(obj);
    public override int GetHashCode() => base.GetHashCode();
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
    
    protected Order() { } // For EF Core
    
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
            throw new InvalidOperationException("Cannot add items to non-draft order");
        
        var item = new OrderItem(Id, productId, quantity, unitPrice);
        _items.Add(item);
        
        RecalculateTotal();
    }
    
    public void PlaceOrder()
    {
        if (Status != OrderStatus.Draft)
            throw new InvalidOperationException("Order already placed");
        
        if (!_items.Any())
            throw new InvalidOperationException("Cannot place order without items");
        
        Status = OrderStatus.Placed;
        
        // Raise domain event
        AddDistributedEvent(new OrderPlacedEvent(Id, CustomerId, TotalAmount));
    }
    
    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled || Status == OrderStatus.Completed)
            throw new InvalidOperationException($"Cannot cancel order in {Status} status");
        
        Status = OrderStatus.Cancelled;
        
        AddDistributedEvent(new OrderCancelledEvent(Id));
    }
    
    private void RecalculateTotal()
    {
        var total = _items.Sum(i => i.TotalPrice.Amount);
        TotalAmount = new Money(total, "USD");
    }
}

public enum OrderStatus
{
    Draft,
    Placed,
    Confirmed,
    Shipped,
    Completed,
    Cancelled
}
```

### Entity with Complex Business Logic

```csharp
public class Product : FullAuditedAggregateRoot<Guid>
{
    public string Name { get; private set; }
    public string Description { get; private set; }
    public Money Price { get; private set; }
    public int StockQuantity { get; private set; }
    public ProductStatus Status { get; private set; }
    
    protected Product() { }
    
    public Product(string name, string description, Money price, int initialStock)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? string.Empty;
        Price = price ?? throw new ArgumentNullException(nameof(price));
        StockQuantity = initialStock >= 0 ? initialStock : throw new ArgumentException("Initial stock cannot be negative");
        Status = ProductStatus.Active;
    }
    
    public void UpdatePrice(Money newPrice)
    {
        if (newPrice == null)
            throw new ArgumentNullException(nameof(newPrice));
        
        if (newPrice.Currency != Price.Currency)
            throw new InvalidOperationException("Cannot change currency");
        
        var oldPrice = Price;
        Price = newPrice;
        
        AddDistributedEvent(new ProductPriceChangedEvent(Id, oldPrice, newPrice));
    }
    
    public void IncreaseStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");
        
        StockQuantity += quantity;
        AddDistributedEvent(new StockIncreasedEvent(Id, quantity, StockQuantity));
    }
    
    public void DecreaseStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");
        
        if (StockQuantity < quantity)
            throw new InvalidOperationException("Insufficient stock");
        
        StockQuantity -= quantity;
        AddDistributedEvent(new StockDecreasedEvent(Id, quantity, StockQuantity));
        
        if (StockQuantity == 0)
        {
            Status = ProductStatus.OutOfStock;
            AddDistributedEvent(new ProductOutOfStockEvent(Id));
        }
    }
    
    public void Discontinue()
    {
        Status = ProductStatus.Discontinued;
        AddDistributedEvent(new ProductDiscontinuedEvent(Id));
    }
}

public enum ProductStatus
{
    Active,
    OutOfStock,
    Discontinued
}
```

### Composite Key Entity

```csharp
public class OrderItem : Entity
{
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money TotalPrice { get; private set; }
    
    protected OrderItem() { }
    
    public OrderItem(Guid orderId, Guid productId, int quantity, Money unitPrice)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");
        
        OrderId = orderId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice ?? throw new ArgumentNullException(nameof(unitPrice));
        TotalPrice = new Money(unitPrice.Amount * quantity, unitPrice.Currency);
    }
    
    public override object?[] GetKeys()
    {
        return new object[] { OrderId, ProductId };
    }
    
    public void UpdateQuantity(int newQuantity)
    {
        if (newQuantity <= 0)
            throw new ArgumentException("Quantity must be positive");
        
        Quantity = newQuantity;
        TotalPrice = new Money(UnitPrice.Amount * newQuantity, UnitPrice.Currency);
    }
}
```

### Using Audited Entities

```csharp
public class BlogPost : FullAuditedAggregateRoot<Guid>
{
    public string Title { get; private set; }
    public string Content { get; private set; }
    public bool IsPublished { get; private set; }
    
    protected BlogPost() { }
    
    public BlogPost(string title, string content)
    {
        Title = title;
        Content = content;
        IsPublished = false;
        // CreatedAt and CreatedBy are automatically set by AuditInterceptor
    }
    
    public void Update(string title, string content)
    {
        Title = title;
        Content = content;
        // ModifiedAt and ModifiedBy are automatically set by AuditInterceptor
    }
    
    public void Publish()
    {
        IsPublished = true;
        AddDistributedEvent(new BlogPostPublishedEvent(Id));
    }
    
    // Soft delete - IsDeleted, DeletedAt, DeletedBy set automatically
}

// Usage
[UnitOfWork]
public async Task CreateBlogPostAsync(CreateBlogPostDto dto)
{
    var post = new BlogPost(dto.Title, dto.Content);
    await _repository.InsertAsync(post);
    // After save, post.CreatedAt and post.CreatedBy will be populated
}
```

## Entity Configuration

### EF Core Configuration

```csharp
public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        
        builder.HasKey(p => p.Id);
        
        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(200);
        
        builder.Property(p => p.Description)
            .HasMaxLength(1000);
        
        // Value Object configuration
        builder.OwnsOne(p => p.Price, price =>
        {
            price.Property(m => m.Amount)
                .HasColumnName("Price_Amount")
                .HasColumnType("decimal(18,2)");
            
            price.Property(m => m.Currency)
                .HasColumnName("Price_Currency")
                .HasMaxLength(3);
        });
        
        builder.Property(p => p.Status)
            .HasConversion<string>();
        
        // Auditing columns
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.CreatedBy).HasMaxLength(256);
        builder.Property(p => p.ModifiedAt);
        builder.Property(p => p.ModifiedBy).HasMaxLength(256);
        
        // Soft delete
        builder.Property(p => p.IsDeleted).IsRequired();
        builder.HasQueryFilter(p => !p.IsDeleted);
        
        // Concurrency
        builder.Property(p => p.ConcurrencyStamp)
            .IsConcurrencyToken()
            .HasMaxLength(40);
        
        // Indexes
        builder.HasIndex(p => p.Name);
        builder.HasIndex(p => p.IsDeleted);
    }
}
```

### Composite Key Configuration

```csharp
public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");
        
        // Composite key
        builder.HasKey(oi => new { oi.OrderId, oi.ProductId });
        
        builder.OwnsOne(oi => oi.UnitPrice, price =>
        {
            price.Property(m => m.Amount).HasColumnName("UnitPrice_Amount");
            price.Property(m => m.Currency).HasColumnName("UnitPrice_Currency");
        });
        
        builder.OwnsOne(oi => oi.TotalPrice, price =>
        {
            price.Property(m => m.Amount).HasColumnName("TotalPrice_Amount");
            price.Property(m => m.Currency).HasColumnName("TotalPrice_Currency");
        });
        
        // Relationships
        builder.HasOne<Order>()
            .WithMany(o => o.Items)
            .HasForeignKey(oi => oi.OrderId);
    }
}
```

## Best Practices

### 1. Encapsulate Business Logic in Entities

```csharp
// ❌ Bad: Anemic domain model
public class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
    public List<OrderItem> Items { get; set; }
}

// Service has all the logic
public class OrderService
{
    public void PlaceOrder(Order order)
    {
        if (order.Items.Count == 0)
            throw new Exception("No items");
        order.Status = OrderStatus.Placed;
    }
}

// ✅ Good: Rich domain model
public class Order : AggregateRoot<Guid>
{
    private readonly List<OrderItem> _items = new();
    public OrderStatus Status { get; private set; }
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    
    public void PlaceOrder()
    {
        if (!_items.Any())
            throw new InvalidOperationException("Cannot place order without items");
        
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}
```

### 2. Use Value Objects for Concepts Without Identity

```csharp
// ✅ Good: Money as Value Object
public class Money : ValueObject
{
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    
    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Amount;
        yield return Currency;
    }
}

// Usage in entity
public class Product : Entity<Guid>
{
    public Money Price { get; private set; }
}
```

### 3. Raise Domain Events for Important Business Events

```csharp
public class Order : AggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        // Validate
        if (!_items.Any())
            throw new InvalidOperationException("Cannot place empty order");
        
        // Change state
        Status = OrderStatus.Placed;
        
        // Raise event for other parts of system to react
        AddDistributedEvent(new OrderPlacedEvent(Id, CustomerId, TotalAmount));
    }
}
```

### 4. Use Appropriate Auditing Level

```csharp
// Simple entity - no auditing
public class Category : Entity<Guid> { }

// Need creation tracking
public class BlogPost : CreationAuditedEntity<Guid> { }

// Need creation and modification tracking
public class Product : AuditedEntity<Guid> { }

// Need soft delete
public class Customer : FullAuditedEntity<Guid> { }
```

### 5. Protect Entity Invariants

```csharp
public class Product : Entity<Guid>
{
    // Private setters
    public string Name { get; private set; }
    public Money Price { get; private set; }
    
    // Protected constructor for EF Core
    protected Product() { }
    
    // Public constructor validates invariants
    public Product(string name, Money price)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required");
        
        if (price == null || price.Amount <= 0)
            throw new ArgumentException("Price must be positive");
        
        Name = name;
        Price = price;
    }
    
    // Methods validate before changing state
    public void UpdatePrice(Money newPrice)
    {
        if (newPrice == null || newPrice.Amount <= 0)
            throw new ArgumentException("Price must be positive");
        
        Price = newPrice;
    }
}
```

## Testing

### Testing Entities

```csharp
public class OrderTests
{
    [Fact]
    public void PlaceOrder_ShouldChangeStatus_AndRaiseEvent()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), "ORD-001");
        order.AddItem(Guid.NewGuid(), 2, new Money(10, "USD"));
        
        // Act
        order.PlaceOrder();
        
        // Assert
        Assert.Equal(OrderStatus.Placed, order.Status);
        var events = order.GetDomainEvents();
        Assert.Single(events);
        Assert.IsType<OrderPlacedEvent>(events.First().Event);
    }
    
    [Fact]
    public void PlaceOrder_ShouldThrow_WhenNoItems()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), "ORD-001");
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => order.PlaceOrder());
    }
}
```

### Testing Value Objects

```csharp
public class MoneyTests
{
    [Fact]
    public void Money_WithSameValues_ShouldBeEqual()
    {
        // Arrange
        var money1 = new Money(100, "USD");
        var money2 = new Money(100, "USD");
        
        // Act & Assert
        Assert.True(money1 == money2);
        Assert.True(money1.ValueEquals(money2));
        Assert.Equal(money1.GetHashCode(), money2.GetHashCode());
    }
    
    [Fact]
    public void Add_ShouldCombineAmounts_WhenSameCurrency()
    {
        // Arrange
        var money1 = new Money(100, "USD");
        var money2 = new Money(50, "USD");
        
        // Act
        var result = money1.Add(money2);
        
        // Assert
        Assert.Equal(150, result.Amount);
        Assert.Equal("USD", result.Currency);
    }
}
```

## Related Features

- **[Repository Pattern](../repository-pattern/README.md)** - Data access for entities
- **[Domain Events](../domain-events/README.md)** - Event handling from aggregates
- **[Unit of Work](../unit-of-work/README.md)** - Transaction management for aggregates

## Common Issues & Solutions

### Issue: Entity changes not persisted

**Cause:** Private setters prevent EF Core from setting values.

**Solution:** Use `protected set` or configure property access in EF Core:

```csharp
builder.Property(p => p.Name).HasField("_name");
```

### Issue: Value object equality not working

**Cause:** Not implementing `GetAtomicValues` correctly.

**Solution:** Ensure all relevant properties are included:

```csharp
protected override IEnumerable<object> GetAtomicValues()
{
    yield return Amount;
    yield return Currency;
    // Include all properties that define equality
}
```

### Issue: Circular reference in aggregate

**Cause:** Navigation properties causing serialization issues.

**Solution:** Use repository to load related aggregates separately.

---

**Next Steps:**
- Learn about [Domain Events](../domain-events/README.md) for aggregate communication
- Explore [Repository Pattern](../repository-pattern/README.md) for data access
- Check [Unit of Work](../unit-of-work/README.md) for transaction management

