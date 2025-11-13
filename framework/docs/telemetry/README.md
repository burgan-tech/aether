# OpenTelemetry

## Overview

Aether provides comprehensive OpenTelemetry integration for distributed tracing, metrics collection, and structured logging. It supports automatic instrumentation, OTLP exporters, and custom instrumentation with minimal configuration.

## Key Features

- **OpenTelemetry Standard** - Industry-standard observability
- **Three Pillars** - Traces, Metrics, and Logs
- **Automatic Instrumentation** - AspNetCore, HttpClient, EF Core
- **OTLP Exporters** - Export to any OTLP-compatible backend
- **Environment Variable Support** - Standard OTEL_* variables
- **Custom Instrumentation** - Extensible builder pattern
- **Header Enrichment** - Automatic context propagation

## Configuration

### Basic Setup

```csharp
services.AddAetherTelemetry(configuration, environment);
```

### With Custom Configuration

```csharp
services.AddAetherTelemetry(configuration, environment, telemetry =>
{
    telemetry.ConfigureTracing((sp, tracing) =>
    {
        tracing.AddSource("MyApp.*");
    });
    
    telemetry.ConfigureMetrics((sp, metrics) =>
    {
        metrics.AddMeter("MyApp.Metrics");
    });
    
    telemetry.ConfigureLogging((sp, logging) =>
    {
        // Custom logging configuration
    });
});
```

### appsettings.json Configuration

```json
{
  "Aether": {
    "Telemetry": {
      "ServiceName": "my-service",
      "ServiceVersion": "1.0.0",
      "ServiceNamespace": "production",
      "TracingEnabled": true,
      "MetricsEnabled": true,
      "LoggingEnabled": true,
      "Otlp": {
        "Endpoint": "http://otel-collector:4318",
        "Protocol": "http/protobuf"
      },
      "Tracing": {
        "EnableAspNetCore": true,
        "EnableHttpClient": true,
        "AdditionalSources": ["MyApp.*"],
        "ExcludedPaths": ["/health", "/metrics"],
        "EnableOtlpExporter": true,
        "EnableConsoleExporter": false
      },
      "Metrics": {
        "EnableAspNetCore": true,
        "EnableHttpClient": true,
        "EnableRuntime": true,
        "EnableProcess": true,
        "AdditionalMeters": ["MyApp.Metrics"],
        "EnableOtlpExporter": true,
        "EnableConsoleExporter": false
      },
      "Logging": {
        "EnableOtlpExporter": true,
        "EnableConsoleExporter": true,
        "ExcludedPaths": ["/health"],
        "IncludeFormattedMessage": true,
        "IncludeScopes": false,
        "ParseStateValues": true,
        "Enrichers": {
          "Headers": ["X-Correlation-Id", "X-User-Id"],
          "CustomAttributes": {
            "deployment.environment": "production",
            "team": "platform"
          }
        }
      }
    }
  }
}
```

### Environment Variables

Standard OpenTelemetry environment variables are supported:

```bash
# Service identification
OTEL_SERVICE_NAME=my-service

# OTLP exporter
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf

# These override appsettings.json values
```

## Distributed Tracing

### Automatic Instrumentation

```csharp
// AspNetCore requests are automatically traced
[HttpGet("{id}")]
public async Task<ProductDto> Get(Guid id)
{
    return await _productService.GetAsync(id); // Traced
}

// HttpClient calls are automatically traced
var response = await _httpClient.GetAsync("https://api.example.com/products");

// EF Core queries are automatically traced (if enabled)
var product = await _repository.GetAsync(id);
```

### Custom Spans with Aspects

```csharp
using BBT.Aether.Aspects;

public class OrderService
{
    [Trace] // Automatic span creation
    public async Task<Order> ProcessOrderAsync(Guid orderId)
    {
        // This method execution is traced
        var order = await _repository.GetAsync(orderId);
        order.Process();
        await _repository.UpdateAsync(order);
        return order;
    }
    
    [Trace(Name = "process-payment", TracingMode = TracingMode.Always)]
    public async Task ProcessPaymentAsync(Payment payment)
    {
        // Custom span name
    }
}
```

### Manual Instrumentation

```csharp
using System.Diagnostics;
using BBT.Aether.Aspects;

public class OrderService
{
    private static readonly ActivitySource ActivitySource = 
        AetherActivitySource.Create("OrderService");
    
    public async Task ProcessOrderAsync(Guid orderId)
    {
        using var activity = ActivitySource.StartActivity("ProcessOrder");
        activity?.SetTag("order.id", orderId);
        
        try
        {
            var order = await _repository.GetAsync(orderId);
            activity?.SetTag("order.status", order.Status);
            
            await ProcessOrderLogicAsync(order);
            
            activity?.SetTag("result", "success");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
```

## Metrics

### Built-in Metrics

Automatically collected:
- HTTP request duration and count
- HTTP client duration and count
- .NET runtime metrics (GC, ThreadPool, etc.)
- Process metrics (CPU, memory)

### Custom Metrics with Aspects

```csharp
public class OrderService
{
    [Metric(Type = MetricType.Counter, Name = "orders.processed")]
    public async Task ProcessOrderAsync(Guid orderId)
    {
        // Counter incremented automatically
        await ProcessOrderLogicAsync(orderId);
    }
    
    [Metric(Type = MetricType.Histogram, Name = "payment.amount")]
    public async Task ProcessPaymentAsync(decimal amount)
    {
        // Histogram tracks payment amounts
    }
}
```

### Manual Metrics

```csharp
using System.Diagnostics.Metrics;
using BBT.Aether.Aspects;

public class OrderMetrics
{
    private static readonly Meter Meter = AetherMeter.Create("OrderService");
    
    private readonly Counter<long> _ordersProcessed;
    private readonly Histogram<double> _orderValue;
    
    public OrderMetrics()
    {
        _ordersProcessed = Meter.CreateCounter<long>(
            "orders.processed",
            description: "Number of orders processed");
        
        _orderValue = Meter.CreateHistogram<double>(
            "order.value",
            unit: "USD",
            description: "Order value in USD");
    }
    
    public void RecordOrderProcessed(Order order)
    {
        _ordersProcessed.Add(1, new KeyValuePair<string, object?>("status", order.Status));
        _orderValue.Record(order.TotalAmount);
    }
}
```

## Structured Logging

### Automatic Enrichment

```csharp
[HttpPost]
public async Task<IActionResult> CreateOrder(CreateOrderDto dto)
{
    // Logs automatically enriched with:
    // - Trace ID
    // - Span ID
    // - Service name
    // - Custom headers (if configured)
    _logger.LogInformation("Creating order for customer {CustomerId}", dto.CustomerId);
    
    return Ok();
}
```

### Custom Log Enrichment with Aspects

```csharp
public class OrderService
{
    [Log(Level = LogLevel.Information)]
    public async Task ProcessOrderAsync([Enrich("orderId")]Guid orderId)
    {
        // Log automatically created with orderId enrichment
        await ProcessOrderLogicAsync(orderId);
    }
}
```

### Manual Structured Logging

```csharp
_logger.LogInformation(
    "Order {OrderId} processed successfully. Status: {Status}, Amount: {Amount}",
    order.Id,
    order.Status,
    order.TotalAmount);

// Results in structured log with fields:
// - OrderId
// - Status
// - Amount
// Plus automatic fields (TraceId, SpanId, etc.)
```

## Context Propagation

### Automatic Propagation

Trace context is automatically propagated:
- HTTP requests (W3C Trace Context headers)
- Dapr service invocation
- Event bus messages

### Manual Propagation

```csharp
public async Task PublishEventAsync(OrderCreatedEvent @event)
{
    var activity = Activity.Current;
    
    // Context is automatically propagated in event headers
    await _eventBus.PublishAsync(@event);
}
```

## Exporters

### OTLP Exporter (Recommended)

Exports to OpenTelemetry Collector or any OTLP-compatible backend:

```json
{
  "Otlp": {
    "Endpoint": "http://otel-collector:4318",
    "Protocol": "http/protobuf"
  }
}
```

Supports:
- Jaeger
- Tempo
- Prometheus
- Loki
- Any OTLP-compatible backend

### Console Exporter (Development)

```json
{
  "Tracing": {
    "EnableConsoleExporter": true
  },
  "Metrics": {
    "EnableConsoleExporter": true
  }
}
```

## Best Practices

### 1. Use Meaningful Span Names

```csharp
// ✅ Good
[Trace(Name = "process-order")]
public async Task ProcessOrderAsync() { }

// ❌ Bad
[Trace(Name = "method1")]
public async Task ProcessOrderAsync() { }
```

### 2. Add Relevant Tags

```csharp
activity?.SetTag("order.id", orderId);
activity?.SetTag("order.status", order.Status);
activity?.SetTag("customer.id", order.CustomerId);
```

### 3. Use Structured Logging

```csharp
// ✅ Good
_logger.LogInformation("Order {OrderId} processed", orderId);

// ❌ Bad
_logger.LogInformation($"Order {orderId} processed");
```

### 4. Exclude Health Checks

```json
{
  "Tracing": {
    "ExcludedPaths": ["/health", "/metrics", "/ready"]
  }
}
```

## Monitoring Dashboards

### Jaeger (Tracing)

View distributed traces:
- Request flows across services
- Span timing and dependencies
- Error traces

### Prometheus + Grafana (Metrics)

Monitor:
- Request rates
- Error rates
- Latency percentiles
- Custom business metrics

### Loki (Logging)

Query structured logs:
- Correlated with traces
- Filtered by attributes
- Time-series analysis

## Testing

```csharp
// Telemetry doesn't interfere with tests
// Traces and metrics are simply not exported in test environment

public class OrderServiceTests
{
    [Fact]
    public async Task ProcessOrder_ShouldSucceed()
    {
        // Test as normal
        // Telemetry is non-invasive
        await _service.ProcessOrderAsync(orderId);
    }
}
```

## Related Features

- **[Application Services](../application-services/README.md)** - Automatically traced
- **[Unit of Work](../unit-of-work/README.md)** - Transaction spans
- **[Distributed Events](../distributed-events/README.md)** - Context propagation

