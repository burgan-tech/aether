# Repository Pattern

## Overview

The Repository Pattern in Aether provides a clean abstraction layer for data access, decoupling your domain logic from data persistence concerns. It offers a consistent API for CRUD operations, querying, and pagination across different data providers.

## Architecture

The repository pattern in Aether is organized in a hierarchical structure:

```
IReadOnlyBasicRepository<TEntity>
    ├── IBasicRepository<TEntity>
    │       ├── IReadOnlyRepository<TEntity>
    │       │       └── IRepository<TEntity>
    │       │               └── IRepository<TEntity, TKey>
    │       └── IBasicRepository<TEntity, TKey>
    └── IReadOnlyBasicRepository<TEntity, TKey>
```

### Design Principles

1. **Separation of Concerns** - Domain layer defines interfaces, Infrastructure provides implementations
2. **Generic Abstractions** - Work with any entity type implementing `IEntity`
3. **Change Tracking Control** - Explicit control over EF Core change tracking
4. **Provider Agnostic** - Support for EF Core, Dapper, MongoDB, etc.
5. **Testability** - Easy to mock for unit testing

## Core Interfaces & Classes

### IReadOnlyBasicRepository<TEntity>

Basic read-only operations for querying entities.

```csharp
public interface IReadOnlyBasicRepository<TEntity> where TEntity : class, IEntity
{
    Task<List<TEntity>> GetListAsync(
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    Task<long> GetCountAsync(CancellationToken cancellationToken = default);
    
    Task<PagedList<TEntity>> GetPagedListAsync(
        PaginationParameters parameters,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
}
```

### IBasicRepository<TEntity>

Extends read-only with basic write operations.

```csharp
public interface IBasicRepository<TEntity> : IReadOnlyBasicRepository<TEntity>
    where TEntity : class, IEntity
{
    Task<TEntity> InsertAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default);
    
    Task<TEntity> UpdateAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default);
    
    Task DeleteAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default);
    
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
```

### IRepository<TEntity>

Full-featured repository with advanced querying capabilities.

```csharp
public interface IRepository<TEntity> : IReadOnlyRepository<TEntity>, IBasicRepository<TEntity>
    where TEntity : class, IEntity
{
    Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    Task<TEntity> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    Task DeleteAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool saveChanges = false,
        CancellationToken cancellationToken = default);
    
    Task DeleteDirectAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool saveChanges = false,
        CancellationToken cancellationToken = default);
}
```

### IRepository<TEntity, TKey>

Repository with typed key for single-key entities.

```csharp
public interface IRepository<TEntity, TKey> : 
    IRepository<TEntity>, 
    IReadOnlyRepository<TEntity, TKey>,
    IBasicRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default);
    
    Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default);
    
    Task DeleteAsync(TKey id, bool saveChanges = false, CancellationToken cancellationToken = default);
}
```

### IReadOnlyRepository<TEntity>

Provides IQueryable access for advanced querying scenarios.

```csharp
public interface IReadOnlyRepository<TEntity> : IReadOnlyBasicRepository<TEntity>
    where TEntity : class, IEntity
{
    Task<IQueryable<TEntity>> GetQueryableAsync();
    
    Task<IQueryable<TEntity>> WithDetailsAsync();
    
    Task<IQueryable<TEntity>> WithDetailsAsync(
        params Expression<Func<TEntity, object>>[] propertySelectors);
}
```

## Base Classes

### BasicRepositoryBase<TEntity>

Abstract base class implementing common repository logic.

```csharp
public abstract class BasicRepositoryBase<TEntity> : 
    IBasicRepository<TEntity>,
    IServiceProviderAccessor
    where TEntity : class, IEntity
{
    public bool? IsChangeTrackingEnabled { get; protected set; }
    public IServiceProvider ServiceProvider { get; }
    public ILazyServiceProvider LazyServiceProvider { get; }
    
    // Abstract methods to be implemented by concrete repositories
    public abstract Task<TEntity> InsertAsync(TEntity entity, bool saveChanges, CancellationToken cancellationToken);
    public abstract Task<TEntity> UpdateAsync(TEntity entity, bool saveChanges, CancellationToken cancellationToken);
    public abstract Task DeleteAsync(TEntity entity, bool saveChanges, CancellationToken cancellationToken);
    public abstract Task SaveChangesAsync(CancellationToken cancellationToken);
    public abstract Task<List<TEntity>> GetListAsync(bool includeDetails, CancellationToken cancellationToken);
    public abstract Task<long> GetCountAsync(CancellationToken cancellationToken);
    
    protected virtual bool ShouldTrackingEntityChange()
    {
        if (!IsChangeTrackingEnabled.HasValue)
        {
            return !UnitOfWorkManager.Current?.IsInActiveUnitOfWork() ?? true;
        }
        return IsChangeTrackingEnabled.Value;
    }
}
```

### RepositoryBase<TEntity>

Extends BasicRepositoryBase with querying capabilities.

```csharp
public abstract class RepositoryBase<TEntity> : 
    BasicRepositoryBase<TEntity>, 
    IRepository<TEntity>
    where TEntity : class, IEntity
{
    public virtual Task<IQueryable<TEntity>> WithDetailsAsync()
    {
        return GetQueryableAsync();
    }
    
    public virtual Task<IQueryable<TEntity>> WithDetailsAsync(
        params Expression<Func<TEntity, object>>[] propertySelectors)
    {
        return GetQueryableAsync();
    }
    
    public abstract Task<IQueryable<TEntity>> GetQueryableAsync();
    
    public abstract Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    public async Task<TEntity> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(predicate, includeDetails, cancellationToken);
        if (entity == null)
        {
            throw new EntityNotFoundException(typeof(TEntity));
        }
        return entity;
    }
}
```

## EF Core Implementation

### EfCoreRepository<TDbContext, TEntity>

Complete EF Core implementation of the repository pattern.

```csharp
public class EfCoreRepository<TDbContext, TEntity> : RepositoryBase<TEntity>, IEfCoreRepository<TEntity>
    where TDbContext : AetherDbContext<TDbContext>
    where TEntity : class, IEntity
{
    public virtual DbSet<TEntity> DbSet => DbContext.Set<TEntity>();
    protected virtual TDbContext DbContext { get; }
    
    public EfCoreRepository(TDbContext dbContext, IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
        DbContext = dbContext;
    }
    
    public override async Task<TEntity> InsertAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default)
    {
        var savedEntity = (await DbSet.AddAsync(entity, cancellationToken)).Entity;
        
        if (saveChanges)
        {
            await DbContext.SaveChangesAsync(cancellationToken);
        }
        
        return savedEntity;
    }
    
    public override async Task<TEntity> UpdateAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default)
    {
        DbContext.Attach(entity);
        var updatedEntity = DbContext.Update(entity).Entity;
        
        if (saveChanges)
        {
            await DbContext.SaveChangesAsync(cancellationToken);
        }
        
        return updatedEntity;
    }
    
    public override async Task DeleteAsync(
        TEntity entity, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default)
    {
        DbSet.Remove(entity);
        
        if (saveChanges)
        {
            await DbContext.SaveChangesAsync(cancellationToken);
        }
    }
    
    public override async Task<IQueryable<TEntity>> GetQueryableAsync()
    {
        return await Task.FromResult(
            ShouldTrackingEntityChange() 
                ? DbSet.AsQueryable() 
                : DbSet.AsNoTracking().AsQueryable()
        );
    }
    
    public override async Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = includeDetails
            ? await WithDetailsAsync()
            : await GetQueryableAsync();
        
        return await queryable
            .Where(predicate)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
```

### EfCoreRepository<TDbContext, TEntity, TKey>

Typed-key variant for entities with single primary keys.

```csharp
public class EfCoreRepository<TDbContext, TEntity, TKey> : 
    EfCoreRepository<TDbContext, TEntity>, 
    IEfCoreRepository<TEntity, TKey>
    where TDbContext : AetherDbContext<TDbContext>
    where TEntity : class, IEntity<TKey>
{
    public EfCoreRepository(TDbContext dbContext, IServiceProvider serviceProvider)
        : base(dbContext, serviceProvider)
    {
    }
    
    public virtual async Task<TEntity> GetAsync(
        TKey id, 
        bool includeDetails = true, 
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, includeDetails, cancellationToken);
        
        if (entity == null)
        {
            throw new EntityNotFoundException(typeof(TEntity), id);
        }
        
        return entity;
    }
    
    public virtual async Task<TEntity?> FindAsync(
        TKey id, 
        bool includeDetails = true, 
        CancellationToken cancellationToken = default)
    {
        return await FindAsync(e => e.Id!.Equals(id), includeDetails, cancellationToken);
    }
    
    public virtual async Task DeleteAsync(
        TKey id, 
        bool saveChanges = false, 
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, false, cancellationToken);
        if (entity == null)
        {
            return;
        }
        
        await DeleteAsync(entity, saveChanges, cancellationToken);
    }
}
```

## Configuration

### Service Registration

Register DbContext with repositories using `AddAetherDbContext`:

```csharp
services.AddAetherDbContext<MyDbContext>(options =>
{
    options.UseNpgsql(configuration.GetConnectionString("Default"));
    options.EnableSensitiveDataLogging(environment.IsDevelopment());
});
```

This automatically registers:
- `MyDbContext` as scoped
- `IRepository<TEntity>` with `EfCoreRepository<MyDbContext, TEntity>`
- `IRepository<TEntity, TKey>` with `EfCoreRepository<MyDbContext, TEntity, TKey>`
- Unit of Work services
- Audit interceptor

### Custom Repository Registration

For custom repositories:

```csharp
// Register custom interface with implementation
services.AddScoped<IProductRepository, ProductRepository>();
```

### AetherDbContext

Your DbContext must inherit from `AetherDbContext<TDbContext>`:

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options)
    {
    }
    
    public DbSet<Product> Products { get; set; }
    public DbSet<Order> Orders { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Configure entities
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyDbContext).Assembly);
    }
}
```

## Usage Examples

### Basic CRUD Operations

```csharp
public class ProductService
{
    private readonly IRepository<Product, Guid> _productRepository;
    
    public ProductService(IRepository<Product, Guid> productRepository)
    {
        _productRepository = productRepository;
    }
    
    // Create
    public async Task<Product> CreateProductAsync(string name, decimal price)
    {
        var product = new Product { Name = name, Price = price };
        return await _productRepository.InsertAsync(product, saveChanges: true);
    }
    
    // Read by ID
    public async Task<Product?> GetProductAsync(Guid id)
    {
        return await _productRepository.FindAsync(id);
    }
    
    // Read with predicate
    public async Task<Product?> FindByNameAsync(string name)
    {
        return await _productRepository.FindAsync(p => p.Name == name);
    }
    
    // Update
    public async Task UpdateProductPriceAsync(Guid id, decimal newPrice)
    {
        var product = await _productRepository.GetAsync(id);
        product.Price = newPrice;
        await _productRepository.UpdateAsync(product, saveChanges: true);
    }
    
    // Delete
    public async Task DeleteProductAsync(Guid id)
    {
        await _productRepository.DeleteAsync(id, saveChanges: true);
    }
}
```

### Querying with Filters

```csharp
public async Task<List<Product>> GetActiveProductsAsync()
{
    return await _productRepository.GetListAsync(
        predicate: p => p.IsActive && !p.IsDeleted,
        includeDetails: false
    );
}

public async Task<long> CountExpensiveProductsAsync()
{
    var queryable = await _productRepository.GetQueryableAsync();
    return await queryable.CountAsync(p => p.Price > 1000);
}
```

### Pagination

```csharp
public async Task<PagedList<Product>> GetProductsPagedAsync(int page, int size)
{
    var parameters = new PaginationParameters
    {
        PageNumber = page,
        PageSize = size,
        Sorting = "Name ASC"
    };
    
    return await _productRepository.GetPagedListAsync(parameters);
}

// Usage
var result = await GetProductsPagedAsync(page: 1, size: 20);
Console.WriteLine($"Total: {result.TotalCount}, Page: {result.PageNumber}/{result.TotalPages}");
foreach (var product in result.Items)
{
    Console.WriteLine($"{product.Name}: ${product.Price}");
}
```

### Advanced Querying with IQueryable

```csharp
public async Task<List<ProductDto>> GetProductsWithCategoryAsync()
{
    var queryable = await _productRepository.GetQueryableAsync();
    
    return await queryable
        .Where(p => p.IsActive)
        .Include(p => p.Category)
        .Select(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            CategoryName = p.Category.Name
        })
        .ToListAsync();
}
```

### Including Related Entities

```csharp
// Automatic includes (if configured in WithDetailsAsync override)
public async Task<Order> GetOrderWithDetailsAsync(Guid orderId)
{
    var queryable = await _orderRepository.WithDetailsAsync();
    return await queryable.FirstOrDefaultAsync(o => o.Id == orderId);
}

// Specific includes
public async Task<Order> GetOrderWithItemsAsync(Guid orderId)
{
    var queryable = await _orderRepository.WithDetailsAsync(
        o => o.OrderItems,
        o => o.Customer
    );
    return await queryable.FirstOrDefaultAsync(o => o.Id == orderId);
}
```

### Batch Operations

```csharp
public async Task CreateMultipleProductsAsync(List<CreateProductDto> dtos)
{
    foreach (var dto in dtos)
    {
        var product = new Product { Name = dto.Name, Price = dto.Price };
        await _productRepository.InsertAsync(product, saveChanges: false);
    }
    
    // Save all changes at once
    await _productRepository.SaveChangesAsync();
}
```

### Delete Direct (Bulk Delete)

```csharp
// Soft delete many entities without loading them
public async Task DeleteExpiredProductsAsync()
{
    await _productRepository.DeleteDirectAsync(
        predicate: p => p.ExpiryDate < DateTime.UtcNow,
        saveChanges: true
    );
}
```

## Change Tracking

### Controlling Change Tracking

```csharp
// Disable change tracking for read-only queries (better performance)
public class ProductReadOnlyRepository : EfCoreRepository<MyDbContext, Product>
{
    public ProductReadOnlyRepository(MyDbContext dbContext, IServiceProvider serviceProvider)
        : base(dbContext, serviceProvider)
    {
        IsChangeTrackingEnabled = false; // All queries will use AsNoTracking
    }
}
```

### Automatic Change Tracking with UnitOfWork

When using Unit of Work, change tracking is automatically managed:
- Inside UoW: Change tracking is enabled
- Outside UoW: Change tracking is disabled (for better performance)

## Best Practices

### 1. Use Appropriate Interface Level

```csharp
// Use IBasicRepository when you only need CRUD
public class SimpleService
{
    private readonly IBasicRepository<Product> _repository;
}

// Use IReadOnlyRepository for query-only scenarios
public class ProductQueryService
{
    private readonly IReadOnlyRepository<Product> _repository;
}

// Use IRepository when you need full features
public class ProductService
{
    private readonly IRepository<Product, Guid> _repository;
}
```

### 2. Avoid Unnecessary SaveChanges

```csharp
// ❌ Bad: Multiple SaveChanges
await _repository.InsertAsync(entity1, saveChanges: true);
await _repository.InsertAsync(entity2, saveChanges: true);

// ✅ Good: Batch and save once
await _repository.InsertAsync(entity1, saveChanges: false);
await _repository.InsertAsync(entity2, saveChanges: false);
await _repository.SaveChangesAsync();

// ✅ Best: Use UnitOfWork
[UnitOfWork]
public async Task CreateMultipleAsync()
{
    await _repository.InsertAsync(entity1);
    await _repository.InsertAsync(entity2);
    // UoW will save all changes on commit
}
```

### 3. Use Pagination for Large Datasets

```csharp
// ❌ Bad: Load all entities
var allProducts = await _repository.GetListAsync();

// ✅ Good: Use pagination
var pagedProducts = await _repository.GetPagedListAsync(new PaginationParameters
{
    PageNumber = 1,
    PageSize = 50
});
```

### 4. Leverage IQueryable for Complex Queries

```csharp
public async Task<List<ProductSummary>> GetProductSummariesAsync(
    string category, 
    decimal minPrice)
{
    var queryable = await _repository.GetQueryableAsync();
    
    return await queryable
        .Where(p => p.Category == category && p.Price >= minPrice)
        .Select(p => new ProductSummary
        {
            Id = p.Id,
            Name = p.Name,
            Price = p.Price
        })
        .ToListAsync();
}
```

### 5. Create Custom Repositories for Complex Logic

```csharp
public interface IOrderRepository : IRepository<Order, Guid>
{
    Task<List<Order>> GetPendingOrdersAsync();
    Task<decimal> GetTotalRevenueAsync(DateTime from, DateTime to);
}

public class OrderRepository : EfCoreRepository<MyDbContext, Order, Guid>, IOrderRepository
{
    public OrderRepository(MyDbContext dbContext, IServiceProvider serviceProvider)
        : base(dbContext, serviceProvider)
    {
    }
    
    public async Task<List<Order>> GetPendingOrdersAsync()
    {
        var queryable = await GetQueryableAsync();
        return await queryable
            .Where(o => o.Status == OrderStatus.Pending)
            .Include(o => o.OrderItems)
            .ToListAsync();
    }
    
    public async Task<decimal> GetTotalRevenueAsync(DateTime from, DateTime to)
    {
        var queryable = await GetQueryableAsync();
        return await queryable
            .Where(o => o.CreatedAt >= from && o.CreatedAt <= to && o.Status == OrderStatus.Completed)
            .SumAsync(o => o.TotalAmount);
    }
}
```

## Testing

### Mocking Repositories

```csharp
public class ProductServiceTests
{
    [Fact]
    public async Task CreateProduct_ShouldInsertAndReturn()
    {
        // Arrange
        var mockRepo = new Mock<IRepository<Product, Guid>>();
        var expectedProduct = new Product { Id = Guid.NewGuid(), Name = "Test" };
        
        mockRepo
            .Setup(r => r.InsertAsync(It.IsAny<Product>(), It.IsAny<bool>(), default))
            .ReturnsAsync(expectedProduct);
        
        var service = new ProductService(mockRepo.Object);
        
        // Act
        var result = await service.CreateProductAsync("Test", 10.99m);
        
        // Assert
        Assert.Equal(expectedProduct.Id, result.Id);
        mockRepo.Verify(r => r.InsertAsync(It.IsAny<Product>(), true, default), Times.Once);
    }
}
```

### In-Memory Database for Integration Tests

```csharp
public class ProductRepositoryTests
{
    private MyDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        return new MyDbContext(options);
    }
    
    [Fact]
    public async Task InsertAsync_ShouldAddEntity()
    {
        // Arrange
        var context = CreateInMemoryContext();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IAmbientUnitOfWorkAccessor>(new AsyncLocalAmbientUowAccessor())
            .BuildServiceProvider();
        var repository = new EfCoreRepository<MyDbContext, Product, Guid>(context, serviceProvider);
        
        var product = new Product { Name = "Test Product", Price = 99.99m };
        
        // Act
        var result = await repository.InsertAsync(product, saveChanges: true);
        
        // Assert
        Assert.NotEqual(Guid.Empty, result.Id);
        var fromDb = await repository.GetAsync(result.Id);
        Assert.Equal("Test Product", fromDb.Name);
    }
}
```

## Related Features

- **[Unit of Work](../unit-of-work/README.md)** - Transaction management with repositories
- **[DDD Building Blocks](../ddd/README.md)** - Entity types to use with repositories
- **[Application Services](../application-services/README.md)** - High-level services using repositories

## Common Issues & Solutions

### Issue: SaveChanges not working inside Unit of Work

**Solution:** When inside a UoW, don't call `SaveChangesAsync` on repository. Let the UoW handle it.

```csharp
[UnitOfWork]
public async Task CreateOrderAsync()
{
    await _repository.InsertAsync(order); // No saveChanges parameter
    // UoW will save on commit
}
```

### Issue: Change tracking causing performance issues

**Solution:** Disable change tracking for read-only operations:

```csharp
// Set at repository level
IsChangeTrackingEnabled = false;

// Or use read-only repository interface
IReadOnlyRepository<Product> _readRepo;
```

### Issue: Entities not loading related data

**Solution:** Use `WithDetailsAsync` or configure includes:

```csharp
public override async Task<IQueryable<Order>> WithDetailsAsync()
{
    return (await GetQueryableAsync())
        .Include(o => o.OrderItems)
        .Include(o => o.Customer);
}
```

---

**Next Steps:**
- Learn about [Unit of Work](../unit-of-work/README.md) for transaction management
- Explore [DDD Building Blocks](../ddd/README.md) for entity design
- Check [Application Services](../application-services/README.md) for higher-level patterns

