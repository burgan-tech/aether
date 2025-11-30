# Aether Framework Documentation

Production-ready .NET SDK for building enterprise applications with Domain-Driven Design and distributed architecture patterns.

## Quick Start

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Core services
builder.Services.AddAetherCore(options => options.ApplicationName = "MyApp");
builder.Services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Event bus
builder.Services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "myapp";
});

// Aspects
builder.Services.AddAetherAspects();

var app = builder.Build();

app.UseAmbientServiceProvider();
app.UseUnitOfWorkMiddleware();
app.MapControllers();

app.Run();
```

## Features

### Core Infrastructure

| Feature | Description |
|---------|-------------|
| [Result Pattern](result-pattern/README.md) | Type-safe error handling, railway-oriented programming |
| [Repository Pattern](repository-pattern/README.md) | Clean data access abstraction with EF Core |
| [Multi-Schema](multi-schema/README.md) | Dynamic schema resolution for multi-tenant apps |
| [Unit of Work](unit-of-work/README.md) | Transaction management with `[UnitOfWork]` attribute |

### Domain-Driven Design

| Feature | Description |
|---------|-------------|
| [DDD Building Blocks](ddd/README.md) | Entities, Value Objects, Aggregate Roots |
| [Domain Events](domain-events/README.md) | Event patterns with automatic dispatching |

### Event Architecture

| Feature | Description |
|---------|-------------|
| [Distributed Events](distributed-events/README.md) | CloudEvents-based event bus with Dapr |
| [Inbox & Outbox](inbox-outbox/README.md) | Reliable messaging patterns |

### Application Layer

| Feature | Description |
|---------|-------------|
| [Application Services](application-services/README.md) | CRUD service patterns with auto-mapping |
| [Aspects](aspects/README.md) | `[Trace]`, `[Log]`, `[Metric]`, `[UnitOfWork]` attributes |

### Infrastructure

| Feature | Description |
|---------|-------------|
| [Distributed Cache](distributed-cache/README.md) | Redis, Dapr state store abstraction |
| [Distributed Lock](distributed-lock/README.md) | Coordinated locking across instances |
| [Background Jobs](background-job/README.md) | Scheduled jobs with Dapr integration |

### Cross-Cutting

| Feature | Description |
|---------|-------------|
| [Object Mapping](mapper/README.md) | AutoMapper integration |
| [GUID Generation](guid-generation/README.md) | Sequential and simple GUID strategies |
| [Telemetry](telemetry/README.md) | OpenTelemetry integration |
| [Response Compression](response-compression/README.md) | Gzip and Brotli compression |
| [HTTP Client](http-client/README.md) | Typed HTTP client abstractions |

## Package Dependencies

```
BBT.Aether.Core (Base)
  ├── BBT.Aether.Domain
  │     └── BBT.Aether.Infrastructure
  │           ├── BBT.Aether.Application
  │           └── BBT.Aether.AspNetCore
  ├── BBT.Aether.Aspects
  └── BBT.Aether.HttpClient
```

## Feature Matrix

| Feature | Core | Domain | Infrastructure | AspNetCore | Aspects |
|---------|:----:|:------:|:--------------:|:----------:|:-------:|
| Result Pattern | ✓ | - | - | ✓ | - |
| Repository | ✓ | ✓ | ✓ | - | - |
| Multi-Schema | ✓ | - | ✓ | ✓ | - |
| Unit of Work | ✓ | - | ✓ | ✓ | ✓ |
| DDD Entities | - | ✓ | - | - | - |
| Domain Events | ✓ | ✓ | ✓ | - | - |
| Distributed Events | ✓ | - | ✓ | ✓ | - |
| Inbox/Outbox | ✓ | ✓ | ✓ | - | - |
| Cache/Lock | ✓ | - | ✓ | - | - |
| Background Jobs | ✓ | ✓ | ✓ | ✓ | - |
| Telemetry | - | - | - | ✓ | ✓ |
| Tracing/Logging/Metrics | - | - | - | - | ✓ |

## Usage Example

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}

public class OrderAppService : CrudAppService<Order, OrderDto, Guid>
{
    [Trace]
    [Log]
    [UnitOfWork]
    public async Task<OrderDto> PlaceOrderAsync(Guid id)
    {
        var order = await Repository.GetAsync(id);
        order.PlaceOrder();
        await Repository.UpdateAsync(order);
        return await MapToGetOutputDtoAsync(order);
    }
}
```

## Version Compatibility

| Aether | .NET | EF Core | Dapr |
|--------|------|---------|------|
| 1.x | 8.0, 9.0 | 8.x, 9.x | 1.13+ |
