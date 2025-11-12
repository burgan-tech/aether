# Object Mapping

## Overview

Aether provides an abstraction layer for object-to-object mapping with built-in AutoMapper integration. It enables clean separation between domain entities and DTOs while maintaining type safety and consistency.

## Key Features

- **Provider Abstraction** - Single interface for all mappers
- **AutoMapper Integration** - Built-in AutoMapper adapter
- **Generic Mapping** - `IObjectMapper<TSource, TDestination>`
- **Non-Generic Mapping** - `IObjectMapper` for dynamic scenarios
- **Application Service Integration** - Automatic mapping in CRUD services

## Core Interfaces

### IObjectMapper

Non-generic mapper interface for dynamic scenarios.

```csharp
public interface IObjectMapper
{
    TDestination Map<TSource, TDestination>(TSource source);
    void Map<TSource, TDestination>(TSource source, TDestination destination);
}
```

### IObjectMapper<TSource, TDestination>

Generic mapper interface for type-specific mapping.

```csharp
public interface IObjectMapper<in TSource, TDestination>
{
    TDestination Map(TSource source);
    TDestination Map(TSource source, TDestination destination);
}
```

## Configuration

### Service Registration

```csharp
services.AddAetherAutoMapperMapper(new List<Type>
{
    typeof(ApplicationMappingProfile),
    typeof(DomainMappingProfile)
});
```

### AutoMapper Profiles

```csharp
public class ApplicationMappingProfile : Profile
{
    public ApplicationMappingProfile()
    {
        // Simple mapping
        CreateMap<Product, ProductDto>();
        CreateMap<ProductDto, Product>();
        
        // Custom mapping
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.CustomerName, opt => opt.MapFrom(s => s.Customer.Name))
            .ForMember(d => d.TotalAmount, opt => opt.MapFrom(s => s.Items.Sum(i => i.TotalPrice)));
        
        // Value object mapping
        CreateMap<Money, decimal>()
            .ConvertUsing(m => m.Amount);
        
        CreateMap<decimal, Money>()
            .ConvertUsing(d => new Money(d, "USD"));
    }
}
```

## Usage Examples

### Basic Mapping

```csharp
public class ProductService
{
    private readonly IObjectMapper _mapper;
    
    public async Task<ProductDto> GetProductAsync(Guid id)
    {
        var product = await _repository.GetAsync(id);
        return _mapper.Map<Product, ProductDto>(product);
    }
    
    public async Task<Product> CreateProductAsync(CreateProductDto dto)
    {
        var product = _mapper.Map<CreateProductDto, Product>(dto);
        await _repository.InsertAsync(product);
        return product;
    }
}
```

### Mapping Collections

```csharp
public async Task<List<ProductDto>> GetProductsAsync()
{
    var products = await _repository.GetListAsync();
    return products.Select(p => _mapper.Map<Product, ProductDto>(p)).ToList();
}
```

### Update Mapping

```csharp
public async Task UpdateProductAsync(Guid id, UpdateProductDto dto)
{
    var product = await _repository.GetAsync(id);
    
    // Map DTO properties to existing entity
    _mapper.Map(dto, product);
    
    await _repository.UpdateAsync(product);
}
```

### Using Generic Mapper

```csharp
public class ProductService
{
    private readonly IObjectMapper<Product, ProductDto> _productMapper;
    private readonly IObjectMapper<CreateProductDto, Product> _createMapper;
    
    public ProductDto GetProduct(Guid id)
    {
        var product = _repository.Get(id);
        return _productMapper.Map(product);
    }
    
    public Product CreateProduct(CreateProductDto dto)
    {
        var product = _createMapper.Map(dto);
        _repository.Insert(product);
        return product;
    }
}
```

## AutoMapper Profiles

### Simple Mappings

```csharp
public class ProductMappingProfile : Profile
{
    public ProductMappingProfile()
    {
        // Two-way mapping
        CreateMap<Product, ProductDto>().ReverseMap();
        
        // One-way mapping
        CreateMap<Product, ProductListDto>();
    }
}
```

### Complex Mappings

```csharp
public class OrderMappingProfile : Profile
{
    public OrderMappingProfile()
    {
        CreateMap<Order, OrderDto>()
            // Map from nested property
            .ForMember(d => d.CustomerName, 
                opt => opt.MapFrom(s => s.Customer.Name))
            
            // Calculated property
            .ForMember(d => d.ItemCount, 
                opt => opt.MapFrom(s => s.Items.Count))
            
            // Custom conversion
            .ForMember(d => d.Status, 
                opt => opt.MapFrom(s => s.Status.ToString()))
            
            // Ignore property
            .ForMember(d => d.TempData, 
                opt => opt.Ignore());
        
        CreateMap<CreateOrderDto, Order>()
            .ConstructUsing(dto => new Order(dto.CustomerName, dto.OrderNumber))
            .ForMember(o => o.Items, opt => opt.Ignore()); // Handle separately
    }
}
```

### Value Object Mappings

```csharp
public class ValueObjectMappingProfile : Profile
{
    public ValueObjectMappingProfile()
    {
        // Money to decimal
        CreateMap<Money, decimal>()
            .ConvertUsing(m => m.Amount);
        
        // Address to string
        CreateMap<Address, string>()
            .ConvertUsing(a => $"{a.Street}, {a.City}, {a.ZipCode}");
        
        // Enum to string
        CreateMap<OrderStatus, string>()
            .ConvertUsing(s => s.ToString());
    }
}
```

## Application Service Integration

### Automatic Mapping in CRUD Services

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    public ProductAppService(
        IServiceProvider serviceProvider,
        IRepository<Product, Guid> repository)
        : base(serviceProvider, repository)
    {
    }
    
    // Mapping happens automatically:
    // - GetAsync maps Product -> ProductDto
    // - CreateAsync maps ProductDto -> Product
    // - UpdateAsync maps ProductDto -> Product
}
```

### Override Mapping

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    protected override Task<Product> MapToEntityAsync(CreateProductDto createInput)
    {
        // Custom mapping logic
        var product = new Product(
            createInput.Name,
            new Money(createInput.Price, createInput.Currency)
        );
        
        return Task.FromResult(product);
    }
    
    protected override Task<ProductDto> MapToGetOutputDtoAsync(Product entity)
    {
        // Custom mapping for output
        var dto = ObjectMapper.Map<Product, ProductDto>(entity);
        dto.ImageUrl = GenerateImageUrl(entity.Id);
        return Task.FromResult(dto);
    }
}
```

## Best Practices

### 1. Use DTOs for API Boundaries

```csharp
// ✅ Good: Separate DTOs for different purposes
public class CreateProductDto { /* Only creation fields */ }
public class UpdateProductDto { /* Only update fields */ }
public class ProductDto { /* All fields for reading */ }
public class ProductListDto { /* Summary fields */ }

// ❌ Bad: Single DTO for everything
public class ProductDto { /* All fields, used everywhere */ }
```

### 2. Keep Profiles Organized

```csharp
// ✅ Good: One profile per module/feature
public class ProductMappingProfile : Profile { }
public class OrderMappingProfile : Profile { }
public class CustomerMappingProfile : Profile { }

// ❌ Bad: Single giant profile
public class ApplicationMappingProfile : Profile
{
    // Hundreds of mappings...
}
```

### 3. Use Constructor Mapping for Immutable Entities

```csharp
public class OrderMappingProfile : Profile
{
    public OrderMappingProfile()
    {
        CreateMap<CreateOrderDto, Order>()
            .ConstructUsing(dto => new Order(
                dto.CustomerId,
                dto.OrderNumber
            ));
    }
}
```

### 4. Validate Mapping Configuration

```csharp
[Fact]
public void AutoMapper_Configuration_IsValid()
{
    var config = new MapperConfiguration(cfg =>
    {
        cfg.AddProfile<ApplicationMappingProfile>();
    });
    
    config.AssertConfigurationIsValid();
}
```

## Testing

### Testing Mappings

```csharp
public class ProductMappingTests
{
    private readonly IMapper _mapper;
    
    public ProductMappingTests()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<ProductMappingProfile>();
        });
        _mapper = config.CreateMapper();
    }
    
    [Fact]
    public void Map_Product_To_ProductDto()
    {
        // Arrange
        var product = new Product(Guid.NewGuid(), "Test Product")
        {
            Price = new Money(99.99m, "USD"),
            Category = "Electronics"
        };
        
        // Act
        var dto = _mapper.Map<Product, ProductDto>(product);
        
        // Assert
        Assert.Equal(product.Id, dto.Id);
        Assert.Equal(product.Name, dto.Name);
        Assert.Equal(99.99m, dto.Price);
    }
}
```

### Testing Service with Mapper

```csharp
public class ProductServiceTests
{
    private readonly Mock<IObjectMapper> _mockMapper;
    private readonly ProductService _service;
    
    [Fact]
    public async Task GetProduct_ShouldMapToDto()
    {
        // Arrange
        var product = new Product(Guid.NewGuid(), "Test");
        var dto = new ProductDto { Id = product.Id, Name = "Test" };
        
        _mockRepository
            .Setup(r => r.GetAsync(product.Id, true, default))
            .ReturnsAsync(product);
        
        _mockMapper
            .Setup(m => m.Map<Product, ProductDto>(product))
            .Returns(dto);
        
        // Act
        var result = await _service.GetProductAsync(product.Id);
        
        // Assert
        Assert.Equal(dto.Id, result.Id);
    }
}
```

## Related Features

- **[Application Services](../application-services/README.md)** - Uses mapper for DTOs
- **[DDD Building Blocks](../ddd/README.md)** - Domain entities to map

