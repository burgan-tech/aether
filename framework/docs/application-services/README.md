# Application Services

## Overview

Ready-to-use base classes for CRUD and read-only services with automatic DTO mapping, pagination, and repository integration. Coordinates between domain layer and presentation layer.

## Quick Start

### Simple CRUD Service

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    public ProductAppService(
        IServiceProvider serviceProvider,
        IRepository<Product, Guid> repository) 
        : base(serviceProvider, repository)
    {
    }
    
    // Inherits: GetAsync, GetListAsync, CreateAsync, UpdateAsync, DeleteAsync
}
```

### Service Registration

```csharp
services.AddAetherApplication();
services.AddScoped<IProductAppService, ProductAppService>();

// Mapper setup
services.AddAetherAutoMapperMapper(new List<Type> { typeof(ApplicationMappingProfile) });
```

## Service Types

### CrudAppService (Full CRUD)

```csharp
// Single DTO for all operations
public class CategoryAppService : CrudAppService<Category, CategoryDto, Guid>
{
}

// Separate DTOs
public class OrderAppService : CrudAppService<
    Order,                          // Entity
    OrderDto,                       // Get DTO
    Guid,                           // Key
    PagedAndSortedResultRequestDto, // List input
    CreateOrderDto,                 // Create DTO
    UpdateOrderDto>                 // Update DTO
{
}
```

### ReadOnlyAppService

```csharp
public class ProductQueryService : ReadOnlyAppService<Product, ProductDto, Guid, ProductFilterDto>
{
    public ProductQueryService(
        IServiceProvider serviceProvider,
        IReadOnlyRepository<Product, Guid> repository) 
        : base(serviceProvider, repository)
    {
    }
}
```

### CrudEntityAppService (No DTOs)

```csharp
public class CategoryService : CrudEntityAppService<Category, Guid>
{
    // Works directly with entities
}
```

## DTOs

```csharp
public class ProductDto : EntityDto<Guid>
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}

public class CreateProductDto
{
    [Required]
    public string Name { get; set; }
    
    [Range(0.01, double.MaxValue)]
    public decimal Price { get; set; }
}

public class ProductFilterDto : PagedAndSortedResultRequestDto
{
    public string? Category { get; set; }
    public decimal? MinPrice { get; set; }
}
```

## Customization

### Override Mapping

```csharp
public class OrderAppService : CrudAppService<Order, OrderDto, Guid, CreateOrderDto, UpdateOrderDto>
{
    protected override Task<Order> MapToEntityAsync(CreateOrderDto input)
    {
        var order = new Order(input.CustomerName);
        foreach (var item in input.Items)
        {
            order.AddItem(item.ProductId, item.Quantity);
        }
        return Task.FromResult(order);
    }
}
```

### Custom Filtering

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid, ProductFilterDto>
{
    protected override async Task<IQueryable<Product>> CreateFilteredQueryAsync(ProductFilterDto input)
    {
        var query = await Repository.GetQueryableAsync();
        
        if (!string.IsNullOrEmpty(input.Category))
            query = query.Where(p => p.Category == input.Category);
        
        if (input.MinPrice.HasValue)
            query = query.Where(p => p.Price >= input.MinPrice.Value);
        
        return query;
    }
}
```

### Add Business Logic

```csharp
public class OrderAppService : CrudAppService<Order, OrderDto, Guid>
{
    [UnitOfWork]
    public async Task<OrderDto> PlaceOrderAsync(Guid id)
    {
        var order = await Repository.GetAsync(id);
        order.PlaceOrder(); // Domain logic
        await Repository.UpdateAsync(order);
        return await MapToGetOutputDtoAsync(order);
    }
}
```

## Mapping Profile

```csharp
public class ApplicationMappingProfile : Profile
{
    public ApplicationMappingProfile()
    {
        CreateMap<Product, ProductDto>();
        CreateMap<CreateProductDto, Product>();
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.Items, opt => opt.MapFrom(s => s.Items));
    }
}
```

## Best Practices

1. **Keep services thin** - Domain logic belongs in entities, not services
2. **Use appropriate service type** - CrudAppService for full CRUD, ReadOnly for queries
3. **Separate DTOs by operation** - CreateDto, UpdateDto, GetDto for clarity
4. **Override only what you need** - Base classes handle common operations

## Related Features

- [Repository Pattern](../repository-pattern/README.md) - Data access
- [Object Mapping](../mapper/README.md) - DTO mapping
- [Unit of Work](../unit-of-work/README.md) - Transaction management
