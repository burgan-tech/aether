# GUID Generation

## Overview

Aether provides pluggable GUID generation strategies for entity identifiers. It includes both simple (random) and sequential (time-based) GUID generators, with sequential being the default for better database performance.

## Key Features

- **IGuidGenerator Interface** - Abstraction for GUID generation
- **SimpleGuidGenerator** - Standard random GUIDs (Guid.NewGuid())
- **SequentialGuidGenerator** - Version 7 UUIDs for better indexing
- **Automatic Integration** - Integrated with AuditInterceptor
- **Configurable** - Easy to switch strategies

## Core Interface

### IGuidGenerator

```csharp
public interface IGuidGenerator
{
    Guid Create();
}
```

## Implementations

### SimpleGuidGenerator

Generates standard random GUIDs.

```csharp
public sealed class SimpleGuidGenerator : IGuidGenerator
{
    public static SimpleGuidGenerator Instance { get; } = new();
    
    public Guid Create()
    {
        return Guid.NewGuid();
    }
}
```

**Characteristics:**
- Random, unordered GUIDs
- Causes index fragmentation in databases
- Good for distributed scenarios without database
- Same as `Guid.NewGuid()`

### SequentialGuidGenerator

Generates version 7 UUIDs (time-ordered).

```csharp
public sealed class SequentialGuidGenerator : IGuidGenerator
{
    public static SequentialGuidGenerator Instance { get; } = new();
    
    public Guid Create()
    {
        return Guid.CreateVersion7(DateTimeOffset.UtcNow);
    }
}
```

**Characteristics:**
- Time-ordered GUIDs
- Better database index performance
- Reduces index fragmentation
- Maintains temporal ordering
- **Default in Aether Infrastructure**

## Configuration

### Default Configuration

```csharp
// Core uses SimpleGuidGenerator
services.AddAetherCore(options => { });

// Infrastructure replaces with SequentialGuidGenerator
services.AddAetherInfrastructure();
```

### Custom Configuration

```csharp
// Use Simple (random)
services.AddSingleton<IGuidGenerator>(SimpleGuidGenerator.Instance);

// Use Sequential (recommended)
services.AddSingleton<IGuidGenerator>(SequentialGuidGenerator.Instance);

// Use custom implementation
services.AddSingleton<IGuidGenerator, CustomGuidGenerator>();
```

## Usage

### Manual GUID Generation

```csharp
public class OrderService
{
    private readonly IGuidGenerator _guidGenerator;
    
    public async Task<Order> CreateOrderAsync()
    {
        var orderId = _guidGenerator.Create();
        var order = new Order(orderId);
        await _repository.InsertAsync(order);
        return order;
    }
}
```

### Automatic Generation with Entities

Entities with `Guid` IDs get automatic GUID generation via `AuditInterceptor`:

```csharp
public class Product : Entity<Guid>
{
    public string Name { get; private set; }
    
    // Constructor without ID - will be generated automatically
    public Product(string name)
    {
        Name = name;
        // Id will be set by AuditInterceptor on insert
    }
}

// Usage
var product = new Product("Test Product"); // Id is default(Guid)
await _repository.InsertAsync(product); // Id is generated here
Console.WriteLine(product.Id); // Now has a value
```

### Manual ID Assignment

```csharp
// Can still assign ID manually if needed
var customId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
var product = new Product(customId, "Test Product");
await _repository.InsertAsync(product); // Uses the provided ID
```

## Integration with EF Core

### AuditInterceptor Integration

The `AuditInterceptor` automatically generates GUIDs for entities:

```csharp
public class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IGuidGenerator _guidGenerator;
    
    private void CheckAndSetId(EntityEntry entry)
    {
        if (entry.Entity is IEntity<Guid> entityWithGuidId)
        {
            TrySetGuidId(entry, entityWithGuidId);
        }
    }
    
    private void TrySetGuidId(EntityEntry entry, IEntity<Guid> entity)
    {
        // Only set if ID is default (empty GUID)
        if (entity.Id != default)
        {
            return;
        }
        
        // Check for DatabaseGeneratedAttribute
        var idProperty = entry.Property("Id").Metadata.PropertyInfo!;
        var dbGeneratedAttr = ReflectionHelper
            .GetSingleAttributeOrDefault<DatabaseGeneratedAttribute>(idProperty);
        
        if (dbGeneratedAttr != null && 
            dbGeneratedAttr.DatabaseGeneratedOption != DatabaseGeneratedOption.None)
        {
            return; // Let database generate
        }
        
        // Generate GUID
        EntityHelper.TrySetId(
            entity,
            () => _guidGenerator.Create(),
            true
        );
    }
}
```

### Database-Generated IDs

If you want the database to generate IDs:

```csharp
public class Product : Entity<Guid>
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public override Guid Id { get; protected set; }
}
```

## Performance Comparison

### Sequential GUIDs (Version 7)

**Advantages:**
- ✅ Better database performance
- ✅ Reduced index fragmentation
- ✅ Maintains insertion order
- ✅ Better for range scans
- ✅ Smaller index size

**Structure:**
```
[timestamp: 48 bits][version: 4 bits][random: 12 bits]
[variant: 2 bits][random: 62 bits]
```

### Random GUIDs

**Advantages:**
- ✅ More random distribution
- ✅ Harder to guess
- ✅ No timing information

**Disadvantages:**
- ❌ Causes index fragmentation
- ❌ Poor database performance
- ❌ Larger index size

## Best Practices

### 1. Use Sequential for Database Entities

```csharp
// ✅ Good: Use default (Sequential) for database entities
services.AddAetherInfrastructure();

public class Order : AuditedAggregateRoot<Guid>
{
    // Sequential GUID will be generated
}
```

### 2. Consider Random for Non-Database Scenarios

```csharp
// For message IDs, correlation IDs, etc.
var correlationId = Guid.NewGuid();
var messageId = SimpleGuidGenerator.Instance.Create();
```

### 3. Don't Mix Strategies in Same Table

```csharp
// ❌ Bad: Mixing strategies causes poor performance
var order1 = new Order { Id = Guid.NewGuid() }; // Random
var order2 = new Order { Id = _guidGenerator.Create() }; // Sequential

// ✅ Good: Consistent strategy
var order1 = new Order(); // Will get sequential GUID
var order2 = new Order(); // Will get sequential GUID
```

### 4. Let Framework Generate IDs

```csharp
// ✅ Good: Let framework generate
var product = new Product("Test");
await _repository.InsertAsync(product);
// product.Id is now set

// ❌ Bad: Manual generation unless needed
var product = new Product("Test");
product.Id = Guid.NewGuid(); // Not recommended
```

## Testing

### Mocking GUID Generator

```csharp
public class OrderServiceTests
{
    private readonly Mock<IGuidGenerator> _mockGuidGenerator;
    private readonly OrderService _service;
    
    [Fact]
    public async Task CreateOrder_ShouldUseGeneratedId()
    {
        // Arrange
        var expectedId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        _mockGuidGenerator
            .Setup(g => g.Create())
            .Returns(expectedId);
        
        // Act
        var order = await _service.CreateOrderAsync();
        
        // Assert
        Assert.Equal(expectedId, order.Id);
    }
}
```

### Testing with Real Generator

```csharp
[Fact]
public void SequentialGuidGenerator_ShouldGenerateUniqueIds()
{
    // Arrange
    var generator = SequentialGuidGenerator.Instance;
    var ids = new HashSet<Guid>();
    
    // Act
    for (int i = 0; i < 1000; i++)
    {
        ids.Add(generator.Create());
    }
    
    // Assert
    Assert.Equal(1000, ids.Count); // All unique
}

[Fact]
public void SequentialGuidGenerator_ShouldGenerateOrderedIds()
{
    // Arrange
    var generator = SequentialGuidGenerator.Instance;
    
    // Act
    var guid1 = generator.Create();
    Thread.Sleep(10); // Ensure different timestamp
    var guid2 = generator.Create();
    
    // Assert
    Assert.True(guid1.CompareTo(guid2) < 0); // guid1 < guid2
}
```

## Custom Implementation

```csharp
public class CustomGuidGenerator : IGuidGenerator
{
    public Guid Create()
    {
        // Your custom logic
        // e.g., incorporate machine ID, data center ID, etc.
        return GenerateCustomGuid();
    }
    
    private Guid GenerateCustomGuid()
    {
        // Custom implementation
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var machineId = GetMachineId();
        var random = Random.Shared.Next();
        
        // Combine into GUID format
        return new Guid(/* ... */);
    }
}

// Register
services.AddSingleton<IGuidGenerator, CustomGuidGenerator>();
```

## Related Features

- **[DDD Building Blocks](../ddd/README.md)** - Entities that use GUID IDs
- **[Repository Pattern](../repository-pattern/README.md)** - Persists entities with GUIDs

## Common Issues

### Issue: IDs not being generated

**Cause:** Not using `AddAetherDbContext` or `AuditInterceptor`.

**Solution:** Ensure proper configuration:

```csharp
services.AddAetherDbContext<MyDbContext>(options => { });
```

### Issue: Random GUIDs causing performance issues

**Cause:** Using `SimpleGuidGenerator` instead of `SequentialGuidGenerator`.

**Solution:** Use infrastructure default:

```csharp
services.AddAetherInfrastructure();
```

