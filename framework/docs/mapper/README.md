# Object Mapping

## Overview

Aether provides an abstraction layer for object-to-object mapping via two separate NuGet packages:

| Package | Default | Description |
|---|---|---|
| `BBT.Aether.Mapperly` | ✅ Yes | Compile-time source generator, zero-reflection, free (MIT) |
| `BBT.Aether.AutoMapper` | No | Runtime reflection, opt-in, requires commercial license (v13+) |

---

## Core Interfaces (`BBT.Aether.Core`)

### IObjectMapper

Non-generic mapper interface for dynamic dispatch scenarios.

```csharp
public interface IObjectMapper
{
    TDestination Map<TSource, TDestination>(TSource source);
    void Map<TSource, TDestination>(TSource source, TDestination destination);
}
```

### IObjectMapper\<TSource, TDestination\>

Generic mapper interface for type-specific injection.

```csharp
public interface IObjectMapper<in TSource, TDestination>
{
    TDestination Map(TSource source);
    TDestination Map(TSource source, TDestination destination);
}
```

---

## Mapperly (Default) — `BBT.Aether.Mapperly`

Mapperly generates mapping code at **compile time** using a Roslyn source generator. There is no runtime reflection — the generated code is plain property assignments, as fast as hand-written mapping.

### Mapperly-Specific Interfaces

```csharp
// One-way mapper with lifecycle hooks
public interface IMapperlyMapper<TSource, TDestination>
{
    TDestination Map(TSource source);
    TDestination Map(TSource source, TDestination destination);
    void BeforeMap(TSource source);
    void AfterMap(TSource source, TDestination destination);
}

// Bidirectional mapper
public interface IReverseMapperlyMapper<TSource, TDestination> : IMapperlyMapper<TSource, TDestination>
{
    TSource ReverseMap(TDestination destination);
    void ReverseMap(TDestination destination, TSource source);
    void BeforeReverseMap(TDestination destination);
    void AfterReverseMap(TDestination destination, TSource source);
}
```

### Base Classes

Inherit from one of these instead of implementing the interfaces directly.

#### `MapperBase<TSource, TDestination>` — One-way

```csharp
public abstract class MapperBase<TSource, TDestination>
    : IMapperlyMapper<TSource, TDestination>, IObjectMapper<TSource, TDestination>
{
    public abstract TDestination Map(TSource source);
    public abstract TDestination Map(TSource source, TDestination destination);
    public virtual void BeforeMap(TSource source) { }
    public virtual void AfterMap(TSource source, TDestination destination) { }
}
```

#### `TwoWayMapperBase<TSource, TDestination>` — Bidirectional

```csharp
public abstract class TwoWayMapperBase<TSource, TDestination>
    : MapperBase<TSource, TDestination>, IReverseMapperlyMapper<TSource, TDestination>
{
    public abstract TSource ReverseMap(TDestination destination);
    public abstract void ReverseMap(TDestination destination, TSource source);
    public virtual void BeforeReverseMap(TDestination destination) { }
    public virtual void AfterReverseMap(TDestination destination, TSource source) { }
}
```

### Service Registration

Pass a list of marker types; assemblies are derived automatically.

```csharp
services.AddAetherMapperlyMapper(
[
    typeof(OrderMapper),
    typeof(UserMapper)
]);
```

The method scans the assemblies for all `IMapperlyMapper<,>` and `IReverseMapperlyMapper<,>` implementations and registers them as singletons. `IObjectMapper` is fulfilled by `MapperlyAdapter`.

### Defining Mappers

#### Simple One-Way Mapper

```csharp
using Riok.Mapperly.Abstractions;

[Mapper]
public partial class ProductMapper : MapperBase<Product, ProductDto>
{
    // Mapperly generates both Map overloads at compile time
    public override partial ProductDto Map(Product source);
    public override partial ProductDto Map(Product source, ProductDto destination);
}
```

#### Mapper with Custom Property Mapping

```csharp
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class OrderMapper : MapperBase<Order, OrderDto>
{
    [MapProperty(nameof(Order.Customer.Name), nameof(OrderDto.CustomerName))]
    [MapperIgnoreTarget(nameof(OrderDto.ItemCount))]
    public override partial OrderDto Map(Order source);

    [MapProperty(nameof(Order.Customer.Name), nameof(OrderDto.CustomerName))]
    [MapperIgnoreTarget(nameof(OrderDto.ItemCount))]
    public override partial OrderDto Map(Order source, OrderDto destination);
}
```

#### Mapper with Lifecycle Hooks

```csharp
[Mapper]
public partial class UserMapper : MapperBase<User, UserDto>
{
    [MapperIgnoreTarget(nameof(UserDto.RoleNames))]
    [MapperIgnoreTarget(nameof(UserDto.IsLockedOut))]
    public override partial UserDto Map(User source);

    [MapperIgnoreTarget(nameof(UserDto.RoleNames))]
    [MapperIgnoreTarget(nameof(UserDto.IsLockedOut))]
    public override partial UserDto Map(User source, UserDto destination);

    public override void AfterMap(User source, UserDto destination)
    {
        destination.RoleNames = source.Roles.Select(r => r.Name).ToList();
    }
}
```

#### Bidirectional Mapper

Register once — the adapter automatically uses `ReverseMap` when mapping in the reverse direction.

```csharp
[Mapper]
public partial class OrderMapper : TwoWayMapperBase<Order, OrderDto>
{
    public override partial OrderDto Map(Order source);
    public override partial OrderDto Map(Order source, OrderDto destination);
    public override partial Order ReverseMap(OrderDto destination);
    public override partial void ReverseMap(OrderDto destination, Order source);
}
```

```csharp
// Forward:  Order → OrderDto
var dto = mapper.Map<Order, OrderDto>(order);

// Reverse:  OrderDto → Order (resolved via IReverseMapperlyMapper automatically)
var order = mapper.Map<OrderDto, Order>(dto);
```

### How the Adapter Dispatches

`MapperlyAdapter` (registered as `IObjectMapper`) follows this resolution order for `Map<TSource, TDestination>`:

1. Resolve `IMapperlyMapper<TSource, TDestination>` → call `BeforeMap` → `Map` → `AfterMap`
2. Resolve `IReverseMapperlyMapper<TDestination, TSource>` → call `BeforeReverseMap` → `ReverseMap` → `AfterReverseMap`
3. Throw `InvalidOperationException` if neither is found

### Using the Mapper

```csharp
// Via non-generic IObjectMapper (dispatches through MapperlyAdapter)
public class ProductService(IObjectMapper mapper)
{
    public ProductDto Get(Product product)
        => mapper.Map<Product, ProductDto>(product);
}

// Via typed IObjectMapper<TSource, TDestination> (direct injection)
public class ProductService(IObjectMapper<Product, ProductDto> mapper)
{
    public ProductDto Get(Product product)
        => mapper.Map(product);
}

// Via typed IMapperlyMapper<TSource, TDestination> (includes hook access)
public class ProductService(IMapperlyMapper<Product, ProductDto> mapper)
{
    public ProductDto Get(Product product)
        => mapper.Map(product);
}
```

---

## AutoMapper (Opt-In) — `BBT.Aether.AutoMapper`

AutoMapper performs mapping at **runtime** using reflection and is suitable for teams that already hold a commercial license.

> **License required:** AutoMapper v13+ is a commercial product. You must configure a valid license key for production use.
>
> **Build-time warning:** Adding `BBT.Aether.AutoMapper` will emit a `NU1903` vulnerability warning because AutoMapper 16.x has a known CVE. This is intentional — it signals that the developer is consciously opting in to a commercial dependency.

### Service Registration

```csharp
services.AddAetherAutoMapperMapper(
    new List<Type>
    {
        typeof(ApplicationMappingProfile),
        typeof(DomainMappingProfile)
    },
    options => options.LicenseKey = configuration["AutoMapper:LicenseKey"]
);
```

The `LicenseKey` is optional — omit it for development/evaluation environments.

### Defining Profiles

```csharp
public class ProductMappingProfile : Profile
{
    public ProductMappingProfile()
    {
        CreateMap<Product, ProductDto>();
        CreateMap<CreateProductDto, Product>();

        CreateMap<Order, OrderDto>()
            .ForMember(d => d.CustomerName, opt => opt.MapFrom(s => s.Customer.Name))
            .ForMember(d => d.ItemCount, opt => opt.MapFrom(s => s.Items.Count));
    }
}
```

---

## Usage Examples

### Basic Mapping

```csharp
public class ProductService(IObjectMapper mapper, IRepository<Product, Guid> repository)
{
    public async Task<ProductDto> GetProductAsync(Guid id)
    {
        var product = await repository.GetAsync(id);
        return mapper.Map<Product, ProductDto>(product);
    }
}
```

### Update Mapping (map onto existing entity)

```csharp
public async Task UpdateProductAsync(Guid id, UpdateProductDto dto)
{
    var product = await repository.GetAsync(id);
    mapper.Map(dto, product);
    await repository.UpdateAsync(product);
}
```

### Collection Mapping

```csharp
var dtos = products.Select(p => mapper.Map<Product, ProductDto>(p)).ToList();
```

---

## Application Service Integration

`ApplicationService`, `AbstractKeyReadOnlyAppService`, and `AbstractKeyCrudAppService` expose `ObjectMapper` (resolves `IObjectMapper` lazily from DI):

```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    public ProductAppService(IServiceProvider sp, IRepository<Product, Guid> repo)
        : base(sp, repo) { }

    // GetAsync    → maps Product → ProductDto automatically
    // CreateAsync → maps CreateProductDto → Product automatically
    // UpdateAsync → maps UpdateProductDto → Product automatically
}
```

Override the protected `MapTo*` methods to customise:

```csharp
protected override Task<ProductDto> MapToGetOutputDtoAsync(Product entity)
{
    var dto = ObjectMapper.Map<Product, ProductDto>(entity);
    dto.ImageUrl = GenerateImageUrl(entity.Id);
    return Task.FromResult(dto);
}
```

---

## Choosing Between Mapperly and AutoMapper

| Criterion | Mapperly (default) | AutoMapper (opt-in) |
|---|---|---|
| Performance | Compile-time, zero-reflection | Runtime reflection |
| License | Free (MIT) | Commercial (v13+) |
| Configuration | `MapperBase` / `TwoWayMapperBase` | `Profile` classes |
| Bidirectional mapping | `TwoWayMapperBase` (single class) | `ReverseMap()` in profile |
| Lifecycle hooks | `BeforeMap` / `AfterMap` | `BeforeMap` / `AfterMap` actions in profile |
| Null safety | Source-generator enforces nullable | Runtime checks |
| Build warnings | None | NU1903 (AutoMapper CVE) |

---

## Related Features

- **[Application Services](../application-services/README.md)** - Uses mapper for DTOs
- **[DDD Building Blocks](../ddd/README.md)** - Domain entities to map
