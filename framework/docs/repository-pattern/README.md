# Repository Pattern

## Overview

Clean data access abstraction supporting EF Core with generic repository interfaces. Provides type-safe CRUD operations, querying, pagination, and change tracking integration.

## Quick Start

### Service Registration

```csharp
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("Default")));

// This registers:
// - IRepository<TEntity, TKey> for each entity
// - IReadOnlyRepository<TEntity, TKey>
// - IAmbientUnitOfWorkAccessor
// - AuditInterceptor
```

### Using Repositories

```csharp
public class ProductService
{
    private readonly IRepository<Product, Guid> _repository;
    
    public ProductService(IRepository<Product, Guid> repository)
    {
        _repository = repository;
    }
    
    public async Task<Product> GetAsync(Guid id)
    {
        return await _repository.GetAsync(id);
    }
    
    [UnitOfWork]
    public async Task CreateAsync(CreateProductDto dto)
    {
        var product = new Product(dto.Name, dto.Price);
        await _repository.InsertAsync(product);
    }
}
```

## Repository Interfaces

### IRepository<TEntity, TKey>

```csharp
public interface IRepository<TEntity, TKey> : IReadOnlyRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken ct = default);
    Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken ct = default);
    Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken ct = default);
    Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken ct = default);
    Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken ct = default);
    Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken ct = default);
    Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken ct = default);
    Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate, bool saveChanges = true, CancellationToken ct = default);
}
```

### IReadOnlyRepository<TEntity, TKey>

```csharp
public interface IReadOnlyRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken ct = default);
    Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken ct = default);
    Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken ct = default);
    Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = false, CancellationToken ct = default);
    Task<long> GetCountAsync(CancellationToken ct = default);
    Task<IQueryable<TEntity>> GetQueryableAsync();
}
```

## Common Operations

### CRUD Operations

```csharp
// Get by ID (throws if not found)
var product = await _repository.GetAsync(id);

// Find by ID (returns null if not found)
var product = await _repository.FindAsync(id);

// Get list with predicate
var products = await _repository.GetListAsync(p => p.Category == "Electronics");

// Insert
await _repository.InsertAsync(product);
    
    // Update
await _repository.UpdateAsync(product);

// Delete by entity
await _repository.DeleteAsync(product);

// Delete by ID
await _repository.DeleteAsync(id);

// Bulk delete with predicate
await _repository.DeleteDirectAsync(p => p.IsExpired);
```

### Pagination

```csharp
public async Task<PagedList<Product>> GetPagedAsync(int page, int pageSize)
{
    var query = await _repository.GetQueryableAsync();
    
    return await query
        .OrderBy(p => p.Name)
        .ToPagedListAsync(page, pageSize);
}
```

### Custom Queries

```csharp
public async Task<List<Product>> GetTopSellingAsync(int count)
{
    var query = await _repository.GetQueryableAsync();
    
    return await query
        .Where(p => p.IsActive)
        .OrderByDescending(p => p.SalesCount)
        .Take(count)
        .ToListAsync();
}
```

## Auto-Save Behavior

```csharp
// Default: Changes tracked, saved with UoW commit
await _repository.InsertAsync(product);

// Immediate save (bypasses UoW)
await _repository.InsertAsync(product, autoSave: true);
```

## DbContext Setup

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>
{
    public DbSet<Product> Products { get; set; }
    public DbSet<Order> Orders { get; set; }
    
    public MyDbContext(DbContextOptions<MyDbContext> options) 
        : base(options)
    {
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyDbContext).Assembly);
    }
}
```

## Best Practices

1. **Use IRepository for writes** - Full CRUD operations
2. **Use IReadOnlyRepository for queries** - Query-only scenarios
3. **Avoid autoSave: true** - Let UoW manage transaction boundaries
4. **Use GetQueryableAsync for complex queries** - Access LINQ directly
5. **Inject repository, not DbContext** - Maintains abstraction

## Related Features

- [Unit of Work](../unit-of-work/README.md) - Transaction management
- [DDD](../ddd/README.md) - Entity base classes
- [Application Services](../application-services/README.md) - Service layer
