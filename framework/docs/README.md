# Aether Framework Documentation

Welcome to the comprehensive documentation for Aether Framework - a production-ready .NET SDK for building enterprise applications with Domain-Driven Design and distributed architecture patterns.

## Table of Contents

- [Core Infrastructure](#core-infrastructure)
- [Domain-Driven Design](#domain-driven-design)
- [Event Architecture](#event-architecture)
- [Application Layer](#application-layer)
- [Infrastructure Services](#infrastructure-services)
- [Cross-Cutting Concerns](#cross-cutting-concerns)
- [Feature Matrix](#feature-matrix)

## Core Infrastructure

### [Result Pattern](result-pattern/README.md)
Type-safe error handling with functional programming patterns for exception-free operations.

**Key Features:**
- Type-safe success/failure representation
- Exception-to-Error automatic conversion
- Monadic operations (Map, Bind, Tap)
- Railway-oriented programming
- Async/await support
- Structured error information
- ASP.NET Core integration

**Quick Start:**
```csharp
// Exception-free error handling
var result = await ResultExtensions.TryAsync(async ct => 
    await userService.GetUserAsync(id, ct));

// Railway pattern
return await GetUserAsync(id)
    .ThenAsync(user => ValidateUserAsync(user))
    .ThenAsync(user => ProcessUserAsync(user))
    .MapAsync(user => user.ToDto());
```

### [Repository Pattern](repository-pattern/README.md)
Clean data access abstraction supporting multiple providers (EF Core, Dapper, MongoDB).

**Key Features:**
- Generic repository interfaces (`IRepository<T>`, `IBasicRepository<T>`)
- Base repository classes for common operations
- EF Core implementation with change tracking
- Read-only repository patterns
- Pagination and filtering support

**Quick Start:**
```csharp
services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(connectionString));
```

### [Unit of Work Pattern](unit-of-work/README.md)
Transactional coordination across multiple data sources with ambient propagation.

**Key Features:**
- Multi-provider transaction management
- AsyncLocal ambient context
- Scope semantics (Required, RequiresNew, Suppress)
- PostSharp [UnitOfWork] attribute
- Middleware integration
- Domain event integration

**Quick Start:**
```csharp
[UnitOfWork]
public async Task CreateOrderAsync(CreateOrderDto dto)
{
    // Automatic transaction management
}
```

## Domain-Driven Design

### [DDD Building Blocks](ddd/README.md)
Complete set of DDD tactical patterns for building rich domain models.

**Key Features:**
- Entity base classes with identity
- Value Object pattern
- Aggregate Root with domain events
- Auditing support (CreatedAt, ModifiedAt, CreatedBy)
- Soft delete support
- Concurrency handling

**Quick Start:**
```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public Money TotalAmount { get; private set; }
    // Rich domain model
}
```

### [Domain Events](domain-events/README.md)
Domain event patterns with automatic dispatching and outbox support.

**Key Features:**
- Domain event collection in aggregates
- Automatic dispatching after commit
- Multiple dispatch strategies
- Event metadata extraction
- Integration with distributed events

**Quick Start:**
```csharp
AddDistributedEvent(new OrderPlacedEvent(Id, TotalAmount));
```

## Event Architecture

### [Distributed Events & Event Bus](distributed-events/README.md)
CloudEvents-based event bus with Dapr integration.

**Key Features:**
- CloudEvent envelope format
- Event naming with attributes
- Automatic handler discovery
- Topic naming strategies
- Environment-based topic prefixing
- Dapr pub/sub integration

**Quick Start:**
```csharp
services.AddAetherEventBus(options =>
{
    options.PubSubName = "pubsub";
    options.DefaultSource = "myapp";
});

await eventBus.PublishAsync(new OrderCreatedEvent(...));
```

### [Inbox & Outbox Pattern](inbox-outbox/README.md)
Transactional messaging patterns for reliable event delivery.

**Key Features:**
- Transactional outbox for guaranteed delivery
- Inbox for idempotent message processing
- Background processors
- Retry policies
- EF Core integration

**Quick Start:**
```csharp
services.AddAetherOutbox<MyDbContext>();
services.AddAetherInbox<MyDbContext>();

modelBuilder.ConfigureOutbox();
modelBuilder.ConfigureInbox();
```

## Application Layer

### [Application Services](application-services/README.md)
CRUD and read-only service patterns with automatic mapping and pagination.

**Key Features:**
- CRUD service base classes
- Read-only service patterns
- Automatic DTO mapping
- Pagination support
- Entity services (no DTOs)
- Auditing integration

**Quick Start:**
```csharp
public class ProductAppService : CrudAppService<Product, ProductDto, Guid>
{
    public ProductAppService(IServiceProvider sp, IRepository<Product, Guid> repo) 
        : base(sp, repo) { }
}
```

## Infrastructure Services

### [Distributed Cache](distributed-cache/README.md)
Multi-provider distributed caching abstraction.

**Key Features:**
- Provider abstraction (Redis, Dapr, .NET Core)
- GetOrSet pattern
- Expiration policies
- JSON serialization
- Consistent API

**Quick Start:**
```csharp
services.AddRedisDistributedCache();
// or
services.AddDaprDistributedCache("statestore");

await cache.GetOrSetAsync("key", async () => await FetchData());
```

### [Distributed Lock](distributed-lock/README.md)
Distributed locking for coordinating work across instances.

**Key Features:**
- Lock acquisition and release
- ExecuteWithLock pattern
- Automatic lock cleanup
- Redis and Dapr implementations
- Configurable expiry

**Quick Start:**
```csharp
services.AddRedisDistributedLock();

await lockService.ExecuteWithLockAsync("resource-id", async () =>
{
    // Critical section
});
```

### [Background Jobs](background-job/README.md)
Scheduled job execution with Dapr Jobs integration.

**Key Features:**
- Job scheduling with cron expressions
- Job handler pattern
- Automatic handler discovery
- Job persistence
- Retry support

**Quick Start:**
```csharp
services.AddDaprJobScheduler<BackgroundJobInfo, JobRepository>(options =>
{
    options.Handlers.Register<MyJobHandler>();
});

await jobService.EnqueueAsync<DaprBackgroundJobOptions, MyJobHandler, MyArgs>(
    new DaprBackgroundJobOptions { Schedule = "@daily" });
```

## Cross-Cutting Concerns

### [Object Mapping](mapper/README.md)
AutoMapper integration for object-to-object mapping.

**Key Features:**
- IObjectMapper abstraction
- AutoMapper adapter
- Generic and typed mappers
- Application service integration

**Quick Start:**
```csharp
services.AddAetherAutoMapperMapper(new List<Type> { typeof(MyProfile) });
```

### [GUID Generation](guid-generation/README.md)
Sequential and simple GUID generation strategies.

**Key Features:**
- IGuidGenerator interface
- SimpleGuidGenerator (Guid.NewGuid)
- SequentialGuidGenerator (Guid.CreateVersion7)
- Automatic entity ID generation

**Quick Start:**
```csharp
services.AddAetherInfrastructure(); // Uses SequentialGuidGenerator
```

### [OpenTelemetry](telemetry/README.md)
Comprehensive observability with traces, metrics, and logs.

**Key Features:**
- OpenTelemetry integration
- Automatic instrumentation
- OTLP exporter support
- Custom instrumentation
- Environment variable configuration
- Header enrichment

**Quick Start:**
```csharp
services.AddAetherTelemetry(configuration, environment);
```

### [Response Compression](response-compression/README.md)
HTTP response compression with Gzip and Brotli.

**Key Features:**
- Gzip compression
- Brotli compression
- MIME type configuration
- Configurable exclusions

**Quick Start:**
```csharp
services.AddAetherAspNetCore();
app.UseAppResponseCompression();
```

### [HTTP Client](http-client/README.md)
Typed HTTP client abstractions with authentication.

**Key Features:**
- IHttpClientWrapper interface
- Configuration-based setup
- Authentication strategies
- Token management
- Default header configuration

**Quick Start:**
```csharp
services.RegisterHttpClient<IMyClient, MyHttpClient>();
```

## Feature Matrix

| Feature | Core | Domain | Infrastructure | AspNetCore | Aspects |
|---------|------|--------|----------------|------------|---------|
| **Result Pattern** | ✓ | - | - | ✓ | - |
| **Repository Pattern** | ✓ | ✓ | ✓ | - | - |
| **Unit of Work** | ✓ | - | ✓ | ✓ | ✓ |
| **Entity & Value Object** | - | ✓ | - | - | - |
| **Aggregate Root** | - | ✓ | - | - | - |
| **Domain Events** | ✓ | ✓ | ✓ | - | - |
| **Distributed Events** | ✓ | - | ✓ | ✓ | - |
| **Inbox/Outbox** | ✓ | ✓ | ✓ | - | - |
| **Application Services** | - | - | - | - | - |
| **Distributed Cache** | ✓ | - | ✓ | - | - |
| **Distributed Lock** | ✓ | - | ✓ | - | - |
| **Background Jobs** | ✓ | ✓ | ✓ | ✓ | - |
| **Object Mapping** | ✓ | - | ✓ | - | - |
| **GUID Generation** | ✓ | - | ✓ | - | - |
| **OpenTelemetry** | - | - | - | ✓ | ✓ |
| **Response Compression** | - | - | - | ✓ | - |
| **HTTP Client** | - | - | - | - | - |

## Package Dependencies

### Project Dependencies

```
BBT.Aether.Core (Base)
    ├── BBT.Aether.Domain
    │   └── BBT.Aether.Infrastructure
    │       ├── BBT.Aether.Application
    │       └── BBT.Aether.AspNetCore
    ├── BBT.Aether.Aspects
    ├── BBT.Aether.HttpClient
    └── BBT.Aether.TestBase
```

### External Dependencies

- **Entity Framework Core** - Data access and migrations
- **Dapr SDK** - Distributed application runtime
- **AutoMapper** - Object mapping
- **OpenTelemetry** - Observability (traces, metrics, logs)
- **PostSharp** - Aspect-oriented programming
- **StackExchange.Redis** - Redis client
- **Npgsql/MySQL/SqlServer** - Database providers

## Getting Help

- **Examples**: Check the `examples/` directory for sample applications
- **Issues**: Report bugs or request features on GitHub
- **Architecture Docs**: See root-level `.md` files for architectural decisions
  - `UnitOfWork_Full_Architecture_Documentation.md`
  - `Dapr_Distributed_Event_Architecture.md`
  - `Domain_Event_Uof_Revision.md`

## Migration Guides

### From Exception-Based to Result Pattern

1. Change method return types from `T` to `Result<T>`
2. Replace `throw` statements with `Result<T>.Fail(Error.*)`
3. Wrap external calls with `ResultExtensions.Try` or `TryAsync`
4. Use railway operators (`ThenAsync`, `BindAsync`) for chaining
5. Convert to ActionResult in controllers using `ToActionResult()`
6. Update error handling from `try/catch` to checking `IsSuccess`

**Example:**
```csharp
// Before
public async Task<User> GetUserAsync(int id)
{
    var user = await _dbContext.Users.FindAsync(id);
    if (user == null)
        throw new EntityNotFoundException(typeof(User), id);
    return user;
}

// After
public async Task<Result<User>> GetUserAsync(int id)
{
    return await ResultExtensions.TryAsync(async ct => 
        await _dbContext.Users.FindAsync(id, ct))
        .EnsureAsync(user => user != null, 
            Error.NotFound("user_not_found", "User not found"));
}
```

### From Custom Repository to Aether

1. Replace custom repository interfaces with `IRepository<TEntity, TKey>`
2. Remove custom repository implementations
3. Configure `AddAetherDbContext` in startup
4. Update service registrations

### Adding Unit of Work

1. Add `services.AddAetherUnitOfWork<TDbContext>()`
2. Add `app.UseUnitOfWorkMiddleware()` to pipeline
3. Decorate service methods with `[UnitOfWork]`

### Enabling Events

1. Add `services.AddAetherEventBus()`
2. Configure event handlers
3. Add event subscriptions endpoint
4. Use `AddDistributedEvent()` in aggregates

## Best Practices

### Repository Usage
- Use `IRepository<T>` for entities with single key
- Use `IRepository<T, TKey>` when you need the key type explicit
- Use `IReadOnlyRepository<T>` for query-only scenarios
- Enable change tracking only when needed

### Unit of Work
- Use `[UnitOfWork]` on application service methods
- Avoid nested UoW in same scope (use Required)
- Use RequiresNew for independent transactions
- Use Suppress for non-transactional operations

### Domain Events
- Raise events from aggregates, not from application services
- Use meaningful event names with past tense
- Include all necessary data in events
- Consider event versioning from the start

### Distributed Events
- Use EventNameAttribute for versioning
- Implement idempotent handlers
- Use inbox for critical handlers
- Monitor outbox processing

### Result Pattern
- Use Result for business logic validation and rules
- Use Try methods for external/infrastructure operations
- Chain operations with railway pattern (ThenAsync, BindAsync)
- Provide meaningful error codes and messages
- Preserve validation errors with Error.Validation
- Convert to ActionResult for API endpoints

### Performance
- Use pagination for large datasets
- Enable query splitting for complex includes
- Use distributed cache for frequently accessed data
- Consider read replicas for read-heavy workloads

## Architecture Principles

Aether Framework is built on these principles:

1. **Clean Architecture** - Clear separation of concerns across layers
2. **Domain-Driven Design** - Rich domain models with business logic
3. **SOLID Principles** - Maintainable and extensible code
4. **Exception-Free Error Handling** - Result pattern for explicit, type-safe error handling
5. **Convention over Configuration** - Sensible defaults with override options
6. **Cloud-Native** - Built for distributed, scalable applications
7. **Observability First** - OpenTelemetry integration from the ground up
8. **Developer Experience** - Intuitive APIs and comprehensive documentation

## Version Compatibility

| Aether Version | .NET Version | EF Core | Dapr |
|---------------|--------------|---------|------|
| 1.x | 8.0, 9.0 | 8.x, 9.x | 1.13+ |

---

**Next Steps:**
1. Explore specific feature documentation above
2. Check out sample applications
3. Join our community discussions
4. Contribute to the project

