# Unit of Work Pattern

## Overview

The Unit of Work (UoW) pattern in Aether provides a comprehensive solution for managing transactions across multiple data sources. It ensures that a set of operations either all succeed or all fail, maintaining data consistency across your application.

The implementation features:
- **Ambient Propagation** - Automatic UoW context sharing across async call chains
- **Multi-Provider Support** - Coordinate transactions across EF Core, Dapper, MongoDB, etc.
- **Scope Semantics** - Fine-grained control with Required, RequiresNew, and Suppress
- **Domain Event Integration** - Automatic event dispatching after successful commits
- **Declarative Control** - PostSharp attributes for clean, aspect-oriented transaction management

## Architecture

### Core Components

```
AsyncLocalAmbientUowAccessor (AsyncLocal storage)
           ↓
    UnitOfWorkManager (Factory/Manager)
           ↓
    CompositeUnitOfWork (Transaction Coordinator)
           ↓
    Multiple ILocalTransactionSource
           ↓
    EfCoreTransactionSource, DapperTransactionSource, etc.
```

### Design Principles

1. **Ambient Context** - AsyncLocal propagation eliminates manual parameter passing
2. **Composition** - Coordinate multiple data sources as a single transaction
3. **Scope-Based Participation** - No ref-counting, clean scope semantics
4. **Deferred Initialization** - Prepare UoW in middleware, initialize in service
5. **Event Integration** - Dispatch domain events after successful commit

## Core Interfaces & Classes

### IUnitOfWork

Main interface representing a unit of work.

```csharp
public interface IUnitOfWork : IAsyncDisposable
{
    Guid Id { get; }
    bool IsDisposed { get; }
    
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
    
    void OnCompleted(Func<IUnitOfWork, Task> handler);
    void OnFailed(Func<IUnitOfWork, Exception?, Task> handler);
    void OnDisposed(Action<IUnitOfWork> handler);
}
```

### IUnitOfWorkManager

Factory for creating and managing UoW instances.

```csharp
public interface IUnitOfWorkManager
{
    IUnitOfWork? Current { get; }
    
    Task<IUnitOfWork> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default);
    
    IUnitOfWork Prepare(string preparationName, bool requiresNew = false);
    
    Task<bool> TryBeginPreparedAsync(
        string preparationName,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

### UnitOfWorkOptions

Configuration for unit of work behavior.

```csharp
public class UnitOfWorkOptions
{
    public const string PrepareName = "HTTP_REQUEST_UOW";
    
    public bool IsTransactional { get; set; } = true;
    public IsolationLevel? IsolationLevel { get; set; }
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;
}
```

### UnitOfWorkScopeOption

Defines scope semantics for UoW creation.

```csharp
public enum UnitOfWorkScopeOption
{
    Required = 0,      // Join existing or create new
    RequiresNew = 1,   // Always create new (nested)
    Suppress = 2       // Non-transactional scope
}
```

### ILocalTransactionSource

Interface for data provider transaction sources.

```csharp
public interface ILocalTransactionSource
{
    string SourceName { get; }
    Task<ILocalTransaction> CreateTransactionAsync(
        UnitOfWorkOptions options,
        CancellationToken cancellationToken = default);
}
```

### ILocalTransaction

Represents an active transaction for a data source.

```csharp
public interface ILocalTransaction
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
```

## Implementation Classes

### AsyncLocalAmbientUowAccessor

Stores current UoW in AsyncLocal for ambient propagation.

```csharp
public sealed class AsyncLocalAmbientUowAccessor : IAmbientUnitOfWorkAccessor
{
    private static readonly AsyncLocal<IUnitOfWork?> _current = new();
    
    public IUnitOfWork? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
    
    public IUnitOfWork? GetActiveUnitOfWork()
    {
        var uow = Current;
        if (uow is UnitOfWorkScope scope)
        {
            return scope.Root.IsAborted ? null : scope.Root;
        }
        return uow;
    }
}
```

### CompositeUnitOfWork

Coordinates transactions across multiple data sources.

```csharp
public sealed class CompositeUnitOfWork : IUnitOfWork, ITransactionalRoot
{
    private readonly IEnumerable<ILocalTransactionSource> _sources;
    private readonly List<(ILocalTransaction tx, ITransactionalLocal? tLocal)> _opened = new();
    private readonly IDomainEventDispatcher? _eventDispatcher;
    
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsInitialized { get; private set; }
    public bool IsAborted { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsDisposed { get; private set; }
    
    public async Task InitializeAsync(
        UnitOfWorkOptions options, 
        CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
            throw new InvalidOperationException("UnitOfWork already initialized");
        
        foreach (var source in _sources)
        {
            var tx = await source.CreateTransactionAsync(options, cancellationToken);
            var tLocal = tx as ITransactionalLocal;
            _opened.Add((tx, tLocal));
        }
        
        IsInitialized = true;
    }
    
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (_, tLocal) in _opened)
        {
            if (tLocal != null)
            {
                await tLocal.SaveChangesAsync(cancellationToken);
            }
        }
    }
    
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (IsAborted)
            throw new InvalidOperationException("Cannot commit aborted UnitOfWork");
        
        // Collect domain events before commit
        var events = new List<DomainEventEnvelope>();
        foreach (var (_, tLocal) in _opened)
        {
            if (tLocal != null)
            {
                events.AddRange(await tLocal.CollectDomainEventsAsync(cancellationToken));
            }
        }
        
        // Commit all transactions
        foreach (var (tx, _) in _opened)
        {
            await tx.CommitAsync(cancellationToken);
        }
        
        IsCompleted = true;
        
        // Dispatch events after successful commit
        if (_eventDispatcher != null && events.Any())
        {
            await _eventDispatcher.DispatchEventsAsync(events, cancellationToken);
        }
        
        // Trigger completed handlers
        foreach (var handler in _completedHandlers)
        {
            await handler(this);
        }
    }
    
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var exceptions = new List<Exception>();
        
        // Rollback in reverse order
        for (int i = _opened.Count - 1; i >= 0; i--)
        {
            try
            {
                await _opened[i].tx.RollbackAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }
        
        IsCompleted = true;
        
        if (exceptions.Any())
        {
            throw new AggregateException("Errors during rollback", exceptions);
        }
    }
    
    public void Abort() => IsAborted = true;
}
```

### UnitOfWorkManager

Manages UoW creation and participation.

```csharp
public sealed class UnitOfWorkManager : IUnitOfWorkManager
{
    private readonly IAmbientUnitOfWorkAccessor _ambient;
    private readonly IServiceProvider _serviceProvider;
    private readonly AetherDomainEventOptions? _domainEventOptions;
    
    public IUnitOfWork? Current => _ambient.GetActiveUnitOfWork();
    
    public async Task<IUnitOfWork> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnitOfWorkOptions();
        
        // Handle Suppress scope
        if (options.Scope == UnitOfWorkScopeOption.Suppress)
        {
            return new SuppressedUowScope(_ambient);
        }
        
        var existing = _ambient.Current as UnitOfWorkScope;
        
        // Handle Required scope - participate in existing UoW
        if (options.Scope == UnitOfWorkScopeOption.Required && existing != null)
        {
            return new UnitOfWorkScope(existing.Root, _ambient);
        }
        
        // Create new root UoW (for RequiresNew or when no existing UoW)
        var sources = _serviceProvider.GetServices<ILocalTransactionSource>();
        var eventDispatcher = _serviceProvider.GetService<IDomainEventDispatcher>();
        var root = new CompositeUnitOfWork(sources, eventDispatcher, _domainEventOptions);
        await root.InitializeAsync(options, cancellationToken);
        
        return new UnitOfWorkScope(root, _ambient);
    }
    
    public IUnitOfWork Prepare(string preparationName, bool requiresNew = false)
    {
        var current = _ambient.Current;
        
        if (current is PreparationUow prep && prep.PreparationName == preparationName)
        {
            return prep;
        }
        
        var newPrep = new PreparationUow(preparationName, requiresNew, _ambient);
        _ambient.Current = newPrep;
        return newPrep;
    }
}
```

### UnitOfWorkScope

Represents a participation scope in a UoW.

```csharp
public sealed class UnitOfWorkScope : IUnitOfWork
{
    private readonly CompositeUnitOfWork _root;
    private readonly IAmbientUnitOfWorkAccessor _ambient;
    private readonly bool _isOwner;
    private readonly IUnitOfWork? _previous;
    
    public Guid Id => _root.Id;
    public bool IsDisposed { get; private set; }
    internal CompositeUnitOfWork Root => _root;
    
    public UnitOfWorkScope(CompositeUnitOfWork root, IAmbientUnitOfWorkAccessor ambient)
    {
        _root = root;
        _ambient = ambient;
        _previous = ambient.Current;
        _isOwner = _previous == null || !ReferenceEquals(_previous, root);
        
        if (_isOwner)
        {
            ambient.Current = this;
        }
    }
    
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => _root.SaveChangesAsync(cancellationToken);
    
    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!_isOwner)
        {
            // Participant scope cannot commit, only owner can
            return Task.CompletedTask;
        }
        return _root.CommitAsync(cancellationToken);
    }
    
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // Any scope can trigger abort
        _root.Abort();
        
        if (_isOwner)
        {
            return _root.RollbackAsync(cancellationToken);
        }
        return Task.CompletedTask;
    }
    
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        
        if (_isOwner)
        {
            // Owner scope: rollback if not completed
            if (!_root.IsCompleted && !_root.IsAborted)
            {
                await _root.RollbackAsync();
            }
            await _root.DisposeAsync();
        }
        
        // Restore previous ambient
        _ambient.Current = _previous;
        IsDisposed = true;
    }
}
```

### EfCoreTransactionSource

EF Core implementation of transaction source.

```csharp
public sealed class EfCoreTransactionSource<TDbContext> : ILocalTransactionSource
    where TDbContext : AetherDbContext<TDbContext>
{
    private readonly TDbContext _dbContext;
    
    public string SourceName => $"efcore:{typeof(TDbContext).Name}";
    
    public EfCoreTransactionSource(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }
    
    public async Task<ILocalTransaction> CreateTransactionAsync(
        UnitOfWorkOptions options,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        
        if (options.IsTransactional)
        {
            transaction = options.IsolationLevel.HasValue
                ? await _dbContext.Database.BeginTransactionAsync(
                    options.IsolationLevel.Value, 
                    cancellationToken)
                : await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }
        
        return new EfCoreLocalTransaction(_dbContext, transaction);
    }
    
    private sealed class EfCoreLocalTransaction : ILocalTransaction, ITransactionalLocal
    {
        private readonly TDbContext _dbContext;
        private readonly IDbContextTransaction? _transaction;
        
        public EfCoreLocalTransaction(TDbContext dbContext, IDbContextTransaction? transaction)
        {
            _dbContext = dbContext;
            _transaction = transaction;
        }
        
        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        
        public async Task<List<DomainEventEnvelope>> CollectDomainEventsAsync(
            CancellationToken cancellationToken = default)
        {
            return await _dbContext.CollectDomainEventsAsync(cancellationToken);
        }
        
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (_transaction != null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }
        }
        
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
        }
    }
}
```

## Configuration

### Service Registration

Register UoW services with your DbContext:

```csharp
// Automatic registration with AddAetherDbContext
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("Default")));

// This registers:
// - IAmbientUnitOfWorkAccessor (singleton)
// - IUnitOfWorkManager (scoped)
// - ILocalTransactionSource for EF Core (scoped)
```

### Manual Registration

For custom scenarios:

```csharp
// Register ambient accessor (singleton)
services.AddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();

// Register UoW manager (scoped)
services.AddScoped<IUnitOfWorkManager, UnitOfWorkManager>();

// Register transaction sources
services.AddScoped<ILocalTransactionSource, EfCoreTransactionSource<MyDbContext>>();
services.AddScoped<ILocalTransactionSource, DapperTransactionSource>();
```

### Domain Event Integration

Configure domain event dispatching:

```csharp
services.AddAetherDomainEventDispatching<MyDbContext>(options =>
{
    options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
});
```

### Middleware Integration

Add UoW middleware to prepare transactions for HTTP requests:

```csharp
app.UseUnitOfWorkMiddleware(options =>
{
    options.IsEnabled = true;
    options.IsTransactional = true;
    options.IsolationLevel = IsolationLevel.ReadCommitted;
    options.Filter = context => 
    {
        // Only for non-GET requests
        return !HttpMethods.IsGet(context.Request.Method);
    };
});
```

## Usage Examples

### Declarative with [UnitOfWork] Attribute

The simplest way to use UoW is with PostSharp attributes:

```csharp
public class OrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IRepository<OrderItem> _orderItemRepository;
    
    [UnitOfWork]
    public async Task<Order> CreateOrderAsync(CreateOrderDto dto)
    {
        // All operations within one transaction
        var order = new Order { CustomerId = dto.CustomerId };
        await _orderRepository.InsertAsync(order);
        
        foreach (var item in dto.Items)
        {
            var orderItem = new OrderItem { OrderId = order.Id, ProductId = item.ProductId };
            await _orderItemRepository.InsertAsync(orderItem);
        }
        
        // Automatic commit on success, rollback on exception
        return order;
    }
}
```

### Programmatic UoW Management

For more control, use `IUnitOfWorkManager`:

```csharp
public class ProductService
{
    private readonly IUnitOfWorkManager _uowManager;
    private readonly IRepository<Product> _productRepository;
    
    public async Task UpdateProductAsync(Guid id, string newName)
    {
        await using var uow = await _uowManager.BeginAsync();
        
        try
        {
            var product = await _productRepository.GetAsync(id);
            product.Name = newName;
            await _productRepository.UpdateAsync(product);
            
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

### Scope Semantics

#### Required (Default)

Participates in existing UoW or creates new one:

```csharp
[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]
public async Task OuterMethodAsync()
{
    // Creates new UoW
    await _repository.InsertAsync(entity1);
    
    // Participates in same UoW
    await InnerMethodAsync();
    
    // All committed together
}

[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]
public async Task InnerMethodAsync()
{
    // Uses ambient UoW from OuterMethodAsync
    await _repository.InsertAsync(entity2);
}
```

#### RequiresNew

Always creates a new, independent UoW:

```csharp
[UnitOfWork]
public async Task OuterAsync()
{
    await _repository.InsertAsync(entity1);
    
    // Creates independent transaction
    await LogActivityAsync();
    
    // If outer fails, log is still committed
}

[UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]
public async Task LogActivityAsync()
{
    // Independent transaction
    await _logRepository.InsertAsync(log);
}
```

#### Suppress

Non-transactional scope:

```csharp
[UnitOfWork]
public async Task ProcessOrderAsync(Guid orderId)
{
    var order = await _orderRepository.GetAsync(orderId);
    
    // This runs outside transaction
    await SendNotificationAsync(order);
    
    await _orderRepository.UpdateAsync(order);
}

[UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]
public async Task SendNotificationAsync(Order order)
{
    // No transaction, immediate execution
    await _emailService.SendAsync(order.CustomerEmail, "Order processed");
}
```

### Multiple Data Sources

Coordinate transactions across EF Core and Dapper:

```csharp
// Register both sources
services.AddScoped<ILocalTransactionSource, EfCoreTransactionSource<MyDbContext>>();
services.AddScoped<ILocalTransactionSource, DapperTransactionSource>();

[UnitOfWork]
public async Task ComplexOperationAsync()
{
    // EF Core operation
    await _efRepository.InsertAsync(entity);
    
    // Dapper operation (uses same transaction)
    await _dapperService.ExecuteAsync("INSERT INTO ...");
    
    // Both committed or rolled back together
}
```

### SaveChanges vs Commit

```csharp
[UnitOfWork]
public async Task CreateOrderWithItemsAsync(CreateOrderDto dto)
{
    // Insert order
    var order = new Order();
    await _orderRepository.InsertAsync(order);
    
    // SaveChanges to get order.Id for items
    await _uowManager.Current!.SaveChangesAsync();
    
    // Insert items with order.Id
    foreach (var item in dto.Items)
    {
        await _orderItemRepository.InsertAsync(new OrderItem 
        { 
            OrderId = order.Id, 
            ProductId = item.ProductId 
        });
    }
    
    // Final commit happens automatically
}
```

### Event Handlers in Transactions

```csharp
public class OrderAggregate : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        // Domain logic
        Status = OrderStatus.Placed;
        
        // Add event
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}

[UnitOfWork]
public async Task PlaceOrderAsync(Guid orderId)
{
    var order = await _repository.GetAsync(orderId);
    order.PlaceOrder(); // Adds event
    await _repository.UpdateAsync(order);
    
    // On commit:
    // 1. Order is saved
    // 2. Events are collected
    // 3. Events are dispatched/written to outbox
}
```

### Prepared UoW with Middleware

Middleware prepares UoW, service initializes it:

```csharp
// Middleware
app.UseUnitOfWorkMiddleware();

// Service
public class ProductService
{
    [UnitOfWork]
    public async Task CreateProductAsync(ProductDto dto)
    {
        // UoW was prepared by middleware
        // Attribute initializes it here
        var product = new Product { Name = dto.Name };
        await _repository.InsertAsync(product);
    }
}
```

### Isolation Levels

Control transaction isolation:

```csharp
[UnitOfWork(IsolationLevel = IsolationLevel.ReadCommitted)]
public async Task NormalOperationAsync()
{
    // Read committed isolation
}

[UnitOfWork(IsolationLevel = IsolationLevel.Serializable)]
public async Task CriticalOperationAsync()
{
    // Highest isolation for critical operations
}
```

### Non-Transactional Operations

Some operations don't need transactions:

```csharp
[UnitOfWork(IsTransactional = false)]
public async Task ReadOnlyQueryAsync()
{
    // No transaction overhead
    var products = await _repository.GetListAsync();
    return products;
}
```

## Best Practices

### 1. Use Appropriate Scope Semantics

```csharp
// ✅ Good: Default Required for most operations
[UnitOfWork]
public async Task StandardOperationAsync() { }

// ✅ Good: RequiresNew for independent logging
[UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]
public async Task LogAsync() { }

// ✅ Good: Suppress for non-transactional operations
[UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]
public async Task SendEmailAsync() { }
```

### 2. Keep Transactions Short

```csharp
// ❌ Bad: Long-running operation in transaction
[UnitOfWork]
public async Task ProcessOrderAsync()
{
    await SaveOrderAsync();
    await SendEmailAsync(); // Blocks transaction
    await UpdateInventoryAsync();
}

// ✅ Good: Only transactional operations
[UnitOfWork]
public async Task ProcessOrderAsync()
{
    await SaveOrderAsync();
    await UpdateInventoryAsync();
}

// Separate non-transactional operations
public async Task AfterOrderProcessedAsync()
{
    await SendEmailAsync();
}
```

### 3. Handle Exceptions Properly

```csharp
[UnitOfWork]
public async Task CreateOrderAsync(CreateOrderDto dto)
{
    try
    {
        var order = new Order();
        await _repository.InsertAsync(order);
        
        // Some operation that might fail
        await _externalService.ValidateAsync(order);
        
        // UoW commits on successful completion
    }
    catch (ValidationException ex)
    {
        // UoW automatically rolls back on exception
        _logger.LogWarning("Order validation failed: {Message}", ex.Message);
        throw; // Re-throw to propagate
    }
}
```

### 4. Don't Mix Manual and Declarative

```csharp
// ❌ Bad: Mixing styles
[UnitOfWork]
public async Task ConfusingAsync()
{
    await using var uow = await _uowManager.BeginAsync(); // Creates nested UoW
    // ...
}

// ✅ Good: Choose one style
[UnitOfWork]
public async Task DeclarativeAsync()
{
    // Let attribute handle it
}

// ✅ Good: Or use manual
public async Task ManualAsync()
{
    await using var uow = await _uowManager.BeginAsync();
    // ...
}
```

### 5. Use SaveChanges When You Need IDs

```csharp
[UnitOfWork]
public async Task CreateOrderWithItemsAsync()
{
    // Insert parent
    var order = new Order();
    await _orderRepository.InsertAsync(order);
    
    // SaveChanges to get generated ID
    await _uowManager.Current!.SaveChangesAsync();
    
    // Now we can use order.Id
    var item = new OrderItem { OrderId = order.Id };
    await _orderItemRepository.InsertAsync(item);
    
    // Commit happens automatically
}
```

## Testing

### Mocking IUnitOfWorkManager

```csharp
public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrder_ShouldCommit_OnSuccess()
    {
        // Arrange
        var mockUow = new Mock<IUnitOfWork>();
        var mockManager = new Mock<IUnitOfWorkManager>();
        
        mockManager
            .Setup(m => m.BeginAsync(null, default))
            .ReturnsAsync(mockUow.Object);
        
        var service = new OrderService(mockManager.Object, ...);
        
        // Act
        await service.CreateOrderAsync(dto);
        
        // Assert
        mockUow.Verify(u => u.CommitAsync(default), Times.Once);
        mockUow.Verify(u => u.RollbackAsync(default), Times.Never);
    }
    
    [Fact]
    public async Task CreateOrder_ShouldRollback_OnError()
    {
        // Arrange
        var mockUow = new Mock<IUnitOfWork>();
        var mockRepository = new Mock<IRepository<Order>>();
        
        mockRepository
            .Setup(r => r.InsertAsync(It.IsAny<Order>(), false, default))
            .ThrowsAsync(new Exception("DB Error"));
        
        // Act & Assert
        await Assert.ThrowsAsync<Exception>(
            () => service.CreateOrderAsync(dto));
        
        mockUow.Verify(u => u.RollbackAsync(default), Times.Once);
    }
}
```

### Integration Testing with UoW

```csharp
public class OrderIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    
    [Fact]
    public async Task CreateOrder_ShouldPersist_WithinTransaction()
    {
        // Arrange
        var scope = _factory.Services.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Order>>();
        
        // Act
        await using var uow = await uowManager.BeginAsync();
        var order = new Order { CustomerName = "Test" };
        await repository.InsertAsync(order);
        await uow.CommitAsync();
        
        // Assert
        var savedOrder = await repository.GetAsync(order.Id);
        Assert.Equal("Test", savedOrder.CustomerName);
    }
}
```

## Related Features

- **[Repository Pattern](../repository-pattern/README.md)** - Data access layer that works with UoW
- **[Domain Events](../domain-events/README.md)** - Events dispatched after UoW commit
- **[DDD Building Blocks](../ddd/README.md)** - Aggregates that use UoW for consistency

## Common Issues & Solutions

### Issue: "UnitOfWork already initialized"

**Cause:** Trying to initialize a UoW that's already been initialized.

**Solution:** Don't call `BeginAsync` inside another UoW. Use `Required` scope to participate.

```csharp
// ❌ Bad
[UnitOfWork]
public async Task OuterAsync()
{
    await using var uow = await _uowManager.BeginAsync(); // Error!
}

// ✅ Good
[UnitOfWork]
public async Task OuterAsync()
{
    await InnerAsync(); // Participates in same UoW
}

[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]
public async Task InnerAsync() { }
```

### Issue: Changes not persisted

**Cause:** Forgetting to commit or exception before commit.

**Solution:** Ensure commit is called or use [UnitOfWork] attribute.

```csharp
// ❌ Bad
await using var uow = await _uowManager.BeginAsync();
await _repository.InsertAsync(entity);
// Missing commit!

// ✅ Good
await using var uow = await _uowManager.BeginAsync();
await _repository.InsertAsync(entity);
await uow.CommitAsync();
```

### Issue: Events not dispatching

**Cause:** Domain event dispatcher not configured.

**Solution:** Configure domain event dispatching:

```csharp
services.AddAetherDomainEventDispatching<MyDbContext>();
```

### Issue: Deadlocks with RequiresNew

**Cause:** Nested transactions accessing same data.

**Solution:** Restructure to avoid nested transactions on same data or use appropriate isolation levels.

---

**Next Steps:**
- Explore [Domain Events](../domain-events/README.md) integration
- Learn about [Repository Pattern](../repository-pattern/README.md) that works with UoW
- Check [DDD Building Blocks](../ddd/README.md) for aggregate patterns

