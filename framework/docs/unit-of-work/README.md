# Unit of Work Pattern

## Overview

Unit of Work (UoW) manages transactions across multiple data sources, ensuring all operations either succeed or fail together. Supports ambient propagation, multiple providers, and declarative control via `[UnitOfWork]` attribute.

## Quick Start

### Service Registration

```csharp
// Automatic registration with AddAetherDbContext
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("Default")));

// Add UoW middleware (optional, for HTTP request scoping)
services.AddAetherUnitOfWork<MyDbContext>();
```

### Middleware Setup

```csharp
var app = builder.Build();

app.UseUnitOfWorkMiddleware(options =>
{
    options.IsEnabled = true;
    options.IsTransactional = true;
    options.Filter = context => !HttpMethods.IsGet(context.Request.Method);
});
```

### Basic Usage with Attribute

```csharp
public class OrderService
{
    [UnitOfWork]
    public async Task<Order> CreateOrderAsync(CreateOrderDto dto)
    {
        var order = new Order(dto);
        await _orderRepository.InsertAsync(order);
        
        foreach (var item in dto.Items)
        {
            await _orderItemRepository.InsertAsync(new OrderItem(order.Id, item));
        }
        
        // Auto-commit on success, auto-rollback on exception
        return order;
    }
}
```

## Scope Options

### Required (Default)

Participates in existing UoW or creates new one:

```csharp
[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]
public async Task OuterAsync()
{
    await _repository.InsertAsync(entity1);
    await InnerAsync(); // Same transaction
}

[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]
public async Task InnerAsync()
{
    await _repository.InsertAsync(entity2); // Uses outer's transaction
}
```

### RequiresNew

Always creates independent transaction:

```csharp
[UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]
public async Task LogActivityAsync()
{
    // Independent transaction - commits even if outer fails
    await _auditRepository.InsertAsync(log);
}
```

### Suppress

Non-transactional scope:

```csharp
[UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]
public async Task SendNotificationAsync()
{
    // Runs outside any transaction
    await _emailService.SendAsync(email);
}
```

## Programmatic Usage

```csharp
public class ProductService
{
    private readonly IUnitOfWorkManager _uowManager;
    
    public async Task UpdateProductAsync(Guid id, string name)
    {
        await using var uow = await _uowManager.BeginAsync();
        
        try
        {
            var product = await _repository.GetAsync(id);
            product.Name = name;
            await _repository.UpdateAsync(product);
            
            await uow.CommitAsync();
        }
        catch
        {
            await uow.RollbackAsync();
            throw;
        }
    }
}
```

## Configuration Options

### UnitOfWorkOptions

```csharp
[UnitOfWork(
    IsTransactional = true,                              // Use DB transaction
    Scope = UnitOfWorkScopeOption.Required,              // Scope behavior
    IsolationLevel = IsolationLevel.ReadCommitted        // Transaction isolation
)]
```

### Middleware Options

```csharp
app.UseUnitOfWorkMiddleware(options =>
{
    options.IsEnabled = true;
    options.IsTransactional = true;
    options.IsolationLevel = IsolationLevel.ReadCommitted;
    options.Filter = context => !HttpMethods.IsGet(context.Request.Method);
});
```

## Domain Events Integration

Domain events are automatically dispatched after successful commit:

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}

[UnitOfWork]
public async Task PlaceOrderAsync(Guid id)
{
    var order = await _repository.GetAsync(id);
    order.PlaceOrder(); // Adds event
    await _repository.UpdateAsync(order);
    
    // On commit: 1) Save order 2) Dispatch events
}
```

## Best Practices

1. **Use `[UnitOfWork]` attribute** - Cleaner than programmatic management
2. **Keep transactions short** - Move non-DB operations outside transaction
3. **Use RequiresNew sparingly** - Only for independent operations like audit logging
4. **Don't nest UoW manually** - Use Scope options instead
5. **Use Suppress for external calls** - Email, HTTP calls shouldn't block transactions

## Related Features

- [Aspects](../aspects/README.md) - `[UnitOfWork]` attribute details
- [Repository Pattern](../repository-pattern/README.md) - Data access with UoW
- [Domain Events](../domain-events/README.md) - Event dispatching after commit
