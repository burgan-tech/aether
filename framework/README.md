# Aether Framework

Aether is a comprehensive .NET SDK designed to accelerate enterprise application development by providing production-ready implementations of common architectural patterns and cross-cutting concerns.

## Overview

Aether Framework provides a solid foundation for building modern, cloud-native applications with:

- **Domain-Driven Design (DDD)** - Complete support for Entities, Value Objects, Aggregates, and Domain Services
- **Repository & Unit of Work** - Clean data access patterns with multi-provider transaction support
- **Distributed Architecture** - Built-in support for distributed events, caching, locking, and background jobs
- **Enterprise Patterns** - Inbox/Outbox patterns, CQRS-ready application services, and more
- **Production-Ready Features** - OpenTelemetry integration, response compression, HTTP client abstractions

## 📚 Documentation

Comprehensive documentation is available in the [docs](docs/) directory:

- **[Documentation Index](docs/README.md)** - Complete feature overview and quick links

### Core Infrastructure
- **[Repository Pattern](docs/repository-pattern/README.md)** - Data access abstraction with EF Core support
- **[Unit of Work](docs/unit-of-work/README.md)** - Transaction management across multiple data sources

### Domain-Driven Design
- **[DDD Building Blocks](docs/ddd/README.md)** - Entities, Value Objects, Aggregates, and Auditing
- **[Domain Events](docs/domain-events/README.md)** - Domain event patterns and dispatching strategies

### Event Architecture
- **[Distributed Events](docs/distributed-events/README.md)** - Event bus with CloudEvents and Dapr integration
- **[Inbox & Outbox](docs/inbox-outbox/README.md)** - Transactional messaging patterns

### Application Layer
- **[Application Services](docs/application-services/README.md)** - CRUD and ReadOnly service patterns with DTOs

### Infrastructure Services
- **[Distributed Cache](docs/distributed-cache/README.md)** - Multi-provider caching (Redis, Dapr, .NET Core)
- **[Distributed Lock](docs/distributed-lock/README.md)** - Distributed locking with Redis and Dapr
- **[Background Jobs](docs/background-job/README.md)** - Scheduled job execution with Dapr

### Cross-Cutting Concerns
- **[Object Mapping](docs/mapper/README.md)** - AutoMapper integration
- **[GUID Generation](docs/guid-generation/README.md)** - Sequential and simple GUID generators
- **[OpenTelemetry](docs/telemetry/README.md)** - Observability with traces, metrics, and logs
- **[Response Compression](docs/response-compression/README.md)** - HTTP response compression
- **[HTTP Client](docs/http-client/README.md)** - Typed HTTP client abstractions

## Project Structure

The solution is organized into focused, layered projects:

- **BBT.Aether.Core** - Base project containing core interfaces, utilities, and abstractions
- **BBT.Aether.Domain** - Domain layer with Entity, Repository interfaces, and domain services
- **BBT.Aether.Infrastructure** - Concrete implementations for all infrastructure concerns
- **BBT.Aether.Application** - Application service base classes and DTOs
- **BBT.Aether.AspNetCore** - ASP.NET Core integrations, middleware, and extensions
- **BBT.Aether.Aspects** - PostSharp cross-cutting aspect implementations
- **BBT.Aether.HttpClient** - HTTP client wrapper and authentication abstractions
- **BBT.Aether.TestBase** - Base classes for integration and unit testing
- **BBT.Aether.Cli** - CLI tool for project scaffolding

## Quick Start

### 1. Install Packages

```bash
dotnet add package BBT.Aether.AspNetCore
dotnet add package BBT.Aether.Infrastructure
```

### 2. Configure Services

```csharp
var builder = WebApplication.CreateBuilder(args);

// Core services
builder.Services.AddAetherCore(options => 
{
    options.ApplicationName = "MyApp";
});

// Infrastructure
builder.Services.AddAetherInfrastructure();

// Database with Repository & UnitOfWork
builder.Services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Application services
builder.Services.AddAetherApplication();

// ASP.NET Core features
builder.Services.AddAetherAspNetCore();

// Event Bus
builder.Services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "myapp";
});

// Telemetry
builder.Services.AddAetherTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

// Middleware
app.UseCorrelationId();
app.UseCurrentUser();
app.UseUnitOfWorkMiddleware();

app.Run();
```

### 3. Create Your Domain

```csharp
public class Product : AuditedAggregateRoot<Guid>
{
    public string Name { get; private set; }
    public Money Price { get; private set; }
    
    public void UpdatePrice(Money newPrice)
    {
        Price = newPrice;
        AddDistributedEvent(new ProductPriceChangedEvent(Id, newPrice));
    }
}
```

## CLI Tool

Scaffold new projects using the Aether CLI:

```bash
dotnet run --project BBT.Aether.Cli create PROJECT_NAME -tm TEAM_NAME -t api -o OUTPUT_PATH
```

## Target Frameworks

- .NET 9.0
- .NET 8.0
- .NET Standard 2.1
- .NET Standard 2.0

## Key Dependencies

- **Entity Framework Core** - Data access
- **Dapr** - Distributed application runtime
- **AutoMapper** - Object-to-object mapping
- **OpenTelemetry** - Observability
- **PostSharp** - Aspect-oriented programming
- **StackExchange.Redis** - Redis client

## Contributing

Contributions are welcome! Please follow the contribution guidelines and refer to the detailed documentation for understanding the architecture and patterns used in the framework.

## License

[Your License Information]
