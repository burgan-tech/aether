# Aspects (AOP)

## Overview

Aether Aspects provides PostSharp-based Aspect-Oriented Programming for cross-cutting concerns. Automatically add tracing, logging, metrics, and transaction management to your methods with simple attributes.

**Package:** `BBT.Aether.Aspects`

## Quick Start

### Installation

```bash
dotnet add package BBT.Aether.Aspects
```

### Service Registration

```csharp
// Program.cs
builder.Services.AddAetherAspects(); // Registers AmbientServiceProvider for aspects

var app = builder.Build();
app.UseAmbientServiceProvider(); // Required middleware
```

### Basic Usage

```csharp
public class OrderService
{
    [Trace]
    [Log]
    [UnitOfWork]
    public async Task<Order> CreateOrderAsync(CreateOrderDto dto)
    {
        // Automatic: span creation, logging, transaction management
        var order = new Order(dto);
        await _repository.InsertAsync(order);
        return order;
    }
}
```

## Aspect Execution Order

Aspects execute in a specific order (outermost to innermost):

```
Request → [Trace] → [Log] → [UnitOfWork] → Method → [UnitOfWork] → [Log] → [Trace] → Response
```

1. **Trace** (outermost) - Creates OpenTelemetry span
2. **Log** - Adds structured logging with scope
3. **UnitOfWork** (innermost) - Manages transaction

## Attributes

### [Trace] - Distributed Tracing

Integrates with OpenTelemetry to create spans for method execution.

```csharp
// Basic span creation
[Trace]
public async Task ProcessAsync() { }

// With custom operation name
[Trace(OperationName = "order.process")]
public async Task ProcessOrderAsync() { }

// With custom tags
[Trace(Tags = new[] { "domain:orders", "priority:high" })]
public async Task ProcessAsync() { }

// Different tracing modes
[Trace(Mode = TracingMode.Span)]     // Creates new span (default)
[Trace(Mode = TracingMode.Event)]    // Adds events to current span
[Trace(Mode = TracingMode.Enrich)]   // Enriches current span with tags
```

**Properties:**
- `Mode` - TracingMode: Span (default), Event, Enrich
- `Kind` - ActivityKind: Internal (default), Server, Client, Producer, Consumer
- `OperationName` - Custom span name (default: ClassName.MethodName)
- `Tags` - Custom tags in "key:value" format

### [Log] - Structured Logging

Adds method entry/exit logging with performance tracking and enrichment.

```csharp
// Basic logging
[Log]
public async Task ProcessAsync() { }

// With arguments logging
[Log(LogArguments = true)]
public async Task ProcessAsync(OrderDto dto) { }

// With return value logging
[Log(LogReturnValue = true)]
public async Task<Order> GetOrderAsync(Guid id) { }

// Custom log level
[Log(Level = LogLevel.Debug)]
public async Task ProcessAsync() { }
```

**Properties:**
- `Level` - LogLevel (default: Information)
- `LogArguments` - Log method arguments (default: false)
- `LogReturnValue` - Log return value (default: false)

**Enrichment with [Enrich] Attribute:**

```csharp
// Enrich specific parameters
public async Task ProcessAsync([Enrich] Guid orderId, [Enrich("Customer")] string customerName)
{
    // Logs will include: orderId, Customer in scope
}

// Enrich properties from complex objects
public class OrderDto
{
    [Enrich("OrderId")]
    public Guid Id { get; set; }
    
    [Enrich]
    public string CustomerName { get; set; }
}

[Log]
public async Task ProcessAsync([Enrich] OrderDto dto)
{
    // Properties marked with [Enrich] are added to log scope
}
```

### [Metric] - OpenTelemetry Metrics

Records method execution metrics (duration, invocations, errors).

```csharp
// Default: Histogram for duration
[Metric]
public async Task ProcessAsync() { }

// Counter for invocations
[Metric(Type = MetricType.Counter)]
public async Task ProcessAsync() { }

// UpDownCounter for in-progress operations
[Metric(Type = MetricType.UpDownCounter)]
public async Task ProcessAsync() { }

// Custom metric name and tags
[Metric(MetricName = "order_processing", Tags = new[] { "service:orders" })]
public async Task ProcessAsync() { }
```

**Properties:**
- `Type` - MetricType: Histogram (default), Counter, UpDownCounter
- `MetricName` - Custom metric name
- `Unit` - Unit of measurement (default: "ms" for Histogram)
- `Tags` - Custom tags in "key:value" format
- `RecordExecutionTime` - Record duration histogram (default: true)
- `RecordInvocationCount` - Record invocation counter (default: true)
- `RecordErrorCount` - Record error counter (default: true)

### [UnitOfWork] - Transaction Management

Wraps method in a database transaction with automatic commit/rollback.

```csharp
// Basic transaction (non-transactional by default, uses SaveChanges)
[UnitOfWork]
public async Task CreateOrderAsync(OrderDto dto) { }

// With explicit transaction
[UnitOfWork(IsTransactional = true)]
public async Task CreateOrderAsync(OrderDto dto) { }

// With isolation level
[UnitOfWork(IsTransactional = true, IsolationLevel = IsolationLevel.ReadCommitted)]
public async Task CreateOrderAsync(OrderDto dto) { }

// Scope options
[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]     // Join existing or create new (default)
[UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]  // Always create new transaction
[UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]     // Non-transactional
```

**Properties:**
- `IsTransactional` - Use database transaction (default: false)
- `Scope` - UnitOfWorkScopeOption (default: Required)
- `IsolationLevel` - Transaction isolation level (optional)

## Configuration

### OpenTelemetry Setup

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("BBT.Aether.Aspects") // Required for [Trace]
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("BBT.Aether.Aspects") // Required for [Metric]
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

### Logging Setup

```csharp
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()); // Required for [Log] enrichment
```

## Combined Usage Example

```csharp
public class OrderAppService
{
    private readonly IRepository<Order, Guid> _repository;
    
    [Trace(Tags = new[] { "domain:orders" })]
    [Log(LogArguments = true)]
    [Metric(Type = MetricType.Histogram)]
    [UnitOfWork(IsTransactional = true)]
    public async Task<OrderDto> CreateOrderAsync([Enrich] CreateOrderDto dto)
    {
        var order = new Order(dto.CustomerName);
        
        foreach (var item in dto.Items)
        {
            order.AddItem(item.ProductId, item.Quantity);
        }
        
        await _repository.InsertAsync(order);
        
        return _mapper.Map<OrderDto>(order);
    }
}
```

## Best Practices

1. **Order matters** - Place attributes in order: `[Trace]`, `[Log]`, `[Metric]`, `[UnitOfWork]`
2. **Don't log sensitive data** - Use `LogArguments = false` (default) for methods with sensitive parameters
3. **Use [Enrich] selectively** - Only enrich parameters needed for troubleshooting
4. **Match metric types to use case** - Histogram for latency, Counter for throughput, UpDownCounter for concurrency
5. **Use RequiresNew sparingly** - Only for truly independent transactions (e.g., audit logging)

## Related Features

- [Unit of Work](../unit-of-work/README.md) - Detailed UoW documentation
- [Telemetry](../telemetry/README.md) - OpenTelemetry setup

