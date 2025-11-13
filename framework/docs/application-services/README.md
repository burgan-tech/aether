# Application Services

## Overview

Application Services in Aether provide ready-to-use base classes for building CRUD and read-only services with automatic DTO mapping, pagination, and repository integration. They follow clean architecture principles by coordinating between domain layer and presentation layer.

## Key Features

- **CRUD Service Patterns** - Full create, read, update, delete operations
- **ReadOnly Service Patterns** - Query-only services
- **Automatic Mapping** - Integration with IObjectMapper
- **Pagination Support** - Built-in paging functionality
- **Flexible DTOs** - Separate DTOs for different operations
- **Repository Integration** - Works seamlessly with repository pattern

## Core Classes

### ApplicationService

Base class for all application services.

```csharp
public abstract class ApplicationService : IServiceProviderAccessor
{
    public IServiceProvider ServiceProvider { get; }
    protected ILazyServiceProvider LazyServiceProvider => LazyGetRequiredService(ref _lazyServiceProvider);
    protected ICurrentUser CurrentUser => LazyGetRequiredService(ref _currentUser);
    protected IObjectMapper ObjectMapper => LazyGetRequiredService(ref _objectMapper);
    
    protected ApplicationService(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }
}
```

### CrudAppService<TEntity, TEntityDto, TKey>

Full CRUD service with single DTO for all operations.

```csharp
public abstract class CrudAppService<TEntity, TEntityDto, TKey> : 
    CrudAppService<TEntity, TEntityDto, TKey, PagedAndSortedResultRequestDto>
    where TEntity : class, IEntity<TKey>
{
    protected CrudAppService(
        IServiceProvider serviceProvider,
        IRepository<TEntity, TKey> repository) 
        : base(serviceProvider, repository)
    {
    }
}
```

### ICrudAppService<TEntityDto, TKey>

Interface for CRUD services.

```csharp
public interface ICrudAppService<TEntityDto, TKey>
{
    Task<TEntityDto> GetAsync(TKey id);
    Task<PagedList<TEntityDto>> GetListAsync(PagedAndSortedResultRequestDto input);
    Task<TEntityDto> CreateAsync(TEntityDto input);
    Task<TEntityDto> UpdateAsync(TKey id, TEntityDto input);
    Task DeleteAsync(TKey id);
}
```

## Service Patterns

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
    
    // Inherits all CRUD methods
    // - GetAsync(id)
    // - GetListAsync(input)
    // - CreateAsync(dto)
    // - UpdateAsync(id, dto)
    // - DeleteAsync(id)
}

// Usage
public class ProductController : ControllerBase
{
    private readonly IProductAppService _productService;
    
    [HttpGet("{id}")]
    public async Task<ProductDto> Get(Guid id)
    {
        return await _productService.GetAsync(id);
    }
    
    [HttpGet]
    public async Task<PagedList<ProductDto>> GetList([FromQuery] PagedAndSortedResultRequestDto input)
    {
        return await _productService.GetListAsync(input);
    }
    
    [HttpPost]
    public async Task<ProductDto> Create(ProductDto dto)
    {
        return await _productService.CreateAsync(dto);
    }
}
```

### CRUD with Separate DTOs

```csharp
public class OrderAppService : CrudAppService<
    Order,               // Entity
    OrderDto,            // Get DTO
    Guid,               // Key
    PagedAndSortedResultRequestDto, // List input
    CreateOrderDto,     // Create DTO
    UpdateOrderDto>     // Update DTO
{
    public OrderAppService(
        IServiceProvider serviceProvider,
        IRepository<Order, Guid> repository) 
        : base(serviceProvider, repository)
    {
    }
    
    // Override mapping if needed
    protected override Task<Order> MapToEntityAsync(CreateOrderDto createInput)
    {
        var order = new Order(
            createInput.CustomerName,
            createInput.OrderNumber
        );
        
        foreach (var item in createInput.Items)
        {
            order.AddItem(item.ProductId, item.Quantity);
        }
        
        return Task.FromResult(order);
    }
}
```

### ReadOnly Service

```csharp
public class ProductQueryService : ReadOnlyAppService<
    Product,
    ProductDto,
    Guid,
    ProductFilterDto>
{
    public ProductQueryService(
        IServiceProvider serviceProvider,
        IReadOnlyRepository<Product, Guid> repository) 
        : base(serviceProvider, repository)
    {
    }
    
    // Custom query method
    public async Task<List<ProductDto>> GetByCategory(string category)
    {
        var products = await Repository.GetListAsync(p => p.Category == category);
        return products.Select(p => ObjectMapper.Map<Product, ProductDto>(p)).ToList();
    }
}
```

### Entity Service (No DTOs)

```csharp
public class CategoryAppService : CrudEntityAppService<Category, Guid>
{
    public CategoryAppService(
        IServiceProvider serviceProvider,
        IRepository<Category, Guid> repository) 
        : base(serviceProvider, repository)
    {
    }
    
    // Works directly with entities - useful for simple entities
}
```

## Configuration

### Service Registration

```csharp
// Register application services
services.AddAetherApplication();

// Register your services
services.AddScoped<IProductAppService, ProductAppService>();
services.AddScoped<IOrderAppService, OrderAppService>();
```

### Mapping Configuration

```csharp
// AutoMapper profiles
public class ApplicationMappingProfile : Profile
{
    public ApplicationMappingProfile()
    {
        CreateMap<Product, ProductDto>();
        CreateMap<ProductDto, Product>();
        
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.Items, opt => opt.MapFrom(s => s.Items));
    }
}

// Register mapper
services.AddAetherAutoMapperMapper(new List<Type> 
{ 
    typeof(ApplicationMappingProfile) 
});
```

## DTOs

### Entity DTO

```csharp
public class ProductDto : EntityDto<Guid>
{
    public string Name { get; set; }
    public string Description { get; set; }
    public decimal Price { get; set; }
    public string Category { get; set; }
}
```

### Create/Update DTOs

```csharp
public class CreateProductDto
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; }
    
    [StringLength(1000)]
    public string Description { get; set; }
    
    [Range(0.01, double.MaxValue)]
    public decimal Price { get; set; }
    
    [Required]
    public string Category { get; set; }
}

public class UpdateProductDto
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; }
    
    [StringLength(1000)]
    public string Description { get; set; }
    
    [Range(0.01, double.MaxValue)]
    public decimal Price { get; set; }
}
```

### Pagination DTOs

```csharp
public class PagedAndSortedResultRequestDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? Sorting { get; set; }
}

public class ProductFilterDto : PagedAndSortedResultRequestDto
{
    public string? Category { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool? InStock { get; set; }
}
```

## Advanced Usage

### Custom Business Logic

```csharp
public class OrderAppService : CrudAppService<Order, OrderDto, Guid>
{
    private readonly IEmailService _emailService;
    
    public OrderAppService(
        IServiceProvider serviceProvider,
        IRepository<Order, Guid> repository,
        IEmailService emailService) 
        : base(serviceProvider, repository)
    {
        _emailService = emailService;
    }
    
    [UnitOfWork]
    public override async Task<OrderDto> CreateAsync(CreateOrderDto input)
    {
        // Custom validation
        await ValidateOrderAsync(input);
        
        // Create entity
        var entity = await MapToEntityAsync(input);
        await Repository.InsertAsync(entity);
        
        // Send notification
        await _emailService.SendOrderConfirmationAsync(entity.CustomerEmail);
        
        return await MapToGetOutputDtoAsync(entity);
    }
    
    [UnitOfWork]
    public async Task PlaceOrderAsync(Guid id)
    {
        var order = await Repository.GetAsync(id);
        order.PlaceOrder(); // Domain logic
        await Repository.UpdateAsync(order);
        
        return await MapToGetOutputDtoAsync(order);
    }
    
    private async Task ValidateOrderAsync(CreateOrderDto input)
    {
        // Custom validation logic
        if (input.Items.Count == 0)
            throw new ValidationException("Order must have at least one item");
    }
}
```

### Overriding Methods

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    // Override default sorting
    protected override IQueryable<Product> ApplyDefaultSorting(IQueryable<Product> query)
    {
        return query.OrderByDescending(p => p.CreatedAt);
    }
    
    // Override mapping
    protected override Task<Product> MapToEntityAsync(CreateProductDto createInput)
    {
        var product = new Product(
            createInput.Name,
            new Money(createInput.Price, "USD")
        );
        return Task.FromResult(product);
    }
    
    // Add authorization
    public override async Task<ProductDto> GetAsync(Guid id)
    {
        await CheckPermissionAsync("Products.View");
        return await base.GetAsync(id);
    }
}
```

### Filtering and Searching

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid, ProductFilterDto>
{
    protected override async Task<IQueryable<Product>> CreateFilteredQueryAsync(ProductFilterDto input)
    {
        var query = await Repository.GetQueryableAsync();
        
        if (!string.IsNullOrEmpty(input.Category))
        {
            query = query.Where(p => p.Category == input.Category);
        }
        
        if (input.MinPrice.HasValue)
        {
            query = query.Where(p => p.Price >= input.MinPrice.Value);
        }
        
        if (input.MaxPrice.HasValue)
        {
            query = query.Where(p => p.Price <= input.MaxPrice.Value);
        }
        
        if (input.InStock.HasValue && input.InStock.Value)
        {
            query = query.Where(p => p.StockQuantity > 0);
        }
        
        return query;
    }
}
```

## Best Practices

### 1. Use Appropriate Service Type

```csharp
// ✅ Good: CRUD for entities requiring all operations
public class ProductAppService : CrudAppService<Product, ProductDto, Guid> { }

// ✅ Good: ReadOnly for query-only scenarios
public class ReportService : ReadOnlyAppService<Report, ReportDto, Guid> { }

// ✅ Good: Entity service for simple entities without DTOs
public class CategoryAppService : CrudEntityAppService<Category, Guid> { }
```

### 2. Separate DTOs for Different Operations

```csharp
// ✅ Good: Separate DTOs provide clarity and validation
public class CreateOrderDto { /* Only required fields for creation */ }
public class UpdateOrderDto { /* Only updatable fields */ }
public class OrderDto { /* All fields for reading */ }
public class OrderListDto { /* Summary fields for list */ }
```

### 3. Keep Services Thin

```csharp
// ✅ Good: Service coordinates, domain has logic
public class OrderAppService : CrudAppService<Order, OrderDto, Guid>
{
    [UnitOfWork]
    public async Task PlaceOrderAsync(Guid id)
    {
        var order = await Repository.GetAsync(id);
        order.PlaceOrder(); // Logic in domain
        await Repository.UpdateAsync(order);
    }
}

// ❌ Bad: Service has business logic
public async Task PlaceOrderAsync(Guid id)
{
    var order = await Repository.GetAsync(id);
    if (order.Status != OrderStatus.Draft)
        throw new Exception("Invalid status");
    order.Status = OrderStatus.Placed; // Should be in domain
    await Repository.UpdateAsync(order);
}
```

## Testing

```csharp
public class ProductAppServiceTests
{
    private readonly Mock<IRepository<Product, Guid>> _mockRepository;
    private readonly Mock<IObjectMapper> _mockMapper;
    private readonly ProductAppService _service;
    
    public ProductAppServiceTests()
    {
        var mockServiceProvider = new Mock<IServiceProvider>();
        _mockRepository = new Mock<IRepository<Product, Guid>>();
        _mockMapper = new Mock<IObjectMapper>();
        
        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IObjectMapper)))
            .Returns(_mockMapper.Object);
        
        _service = new ProductAppService(mockServiceProvider.Object, _mockRepository.Object);
    }
    
    [Fact]
    public async Task GetAsync_ShouldReturnDto()
    {
        // Arrange
        var product = new Product(Guid.NewGuid(), "Test Product");
        var dto = new ProductDto { Id = product.Id, Name = "Test Product" };
        
        _mockRepository
            .Setup(r => r.GetAsync(product.Id, true, default))
            .ReturnsAsync(product);
        
        _mockMapper
            .Setup(m => m.Map<Product, ProductDto>(product))
            .Returns(dto);
        
        // Act
        var result = await _service.GetAsync(product.Id);
        
        // Assert
        Assert.Equal(dto.Id, result.Id);
        Assert.Equal(dto.Name, result.Name);
    }
}
```

## Related Features

- **[Repository Pattern](../repository-pattern/README.md)** - Data access
- **[Object Mapping](../mapper/README.md)** - DTO mapping
- **[DDD Building Blocks](../ddd/README.md)** - Domain entities
- **[Unit of Work](../unit-of-work/README.md)** - Transaction management

