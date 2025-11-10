# Dapr Distributed Event Architecture — Full Implementation Design

## 1. Overview

This document describes a **reflection-minimized**, **Dapr-compatible distributed event architecture** supporting **CloudEvent 1.0**, **Domain Event publishing**, and **Controller-based handling**.

It provides:
- Unified event handling via `IEventHandler<T>`
- Mandatory `[EventName]` attribute to describe event metadata
- Automatic Dapr subscription discovery
- Zero runtime reflection (only startup-time scanning)
- UoW and Outbox/Inbox compatible event publishing
- Open-generic CloudEvent and DomainEvent publishers
- Highly extensible and observable pattern

---

## 2. Architecture Goals

| Goal | Description |
|------|--------------|
| **Minimal Reflection** | Reflection only during startup scanning for handlers. |
| **Dapr Compatibility** | Directly compatible with Dapr pub/sub (CloudEvent + `/dapr/subscribe`). |
| **Unified Interface** | All handlers implement `IEventHandler<T>`. |
| **Static Metadata** | `[EventName]` provides all required metadata: name, version, pubsub, topic. |
| **UoW Integration** | Domain events published automatically on commit. |
| **Performance** | Invokers are compiled delegates (no runtime reflection). |
| **Extensibility** | Handlers, buses, and serializers can be swapped. |

---

## 3. Core Components

### 3.1 EventName Attribute

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventNameAttribute : Attribute
{
    public string Name { get; }
    public int Version { get; }
    public string? PubSub { get; }
    public string? Topic { get; }
    public string? DataSchema { get; }
}
```

Each event must be decorated with `[EventName]`. Example:

```csharp
[EventName("OrderCreated", version: 1, pubSub: "pubsub", topic: "order.created.v1")]
public sealed record OrderCreatedDomainEvent(Guid OrderId, decimal Amount);
```

### 3.2 EventMeta Static Cache

Reads `[EventName]` once per event type. No reflection in runtime.

```csharp
public static class EventMeta<T>
{
    public static readonly string Name;
    public static readonly int Version;
    public static readonly string PubSub;
    public static readonly string Topic;
    public static readonly string? DataSchema;
}
```

---

## 4. CloudEvent and Serialization

### 4.1 CloudEventEnvelope

Implements CloudEvent 1.0 structure.

```csharp
public sealed class CloudEventEnvelope<T>
{
    public string SpecVersion { get; init; } = "1.0";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Source { get; init; } = "aether://myapp";
    public string Type { get; init; } = typeof(T).FullName!;
    public T? Data { get; init; }
    public string? DataSchema { get; init; }
}
```

### 4.2 Serializer

```csharp
public sealed class SystemTextJsonEventSerializer : IEventSerializer
{
    public byte[] Serialize<T>(CloudEventEnvelope<T> env) => 
        JsonSerializer.SerializeToUtf8Bytes(env);
    public CloudEventEnvelope<T> Deserialize<T>(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<CloudEventEnvelope<T>>(bytes)!;
}
```

---

## 5. Distributed Event Bus (Dapr)

```csharp
public sealed class DaprDistributedEventBus : IDistributedEventBus
{
    private readonly DaprClient _dapr;
    private readonly IEventSerializer _ser;

    public async Task PublishAsync<T>(CloudEventEnvelope<T> envelope, CancellationToken ct = default)
    {
        var bytes = _ser.Serialize(envelope);
        await _dapr.PublishEventRawAsync(EventMeta<T>.PubSub, EventMeta<T>.Topic, bytes, "application/cloudevents+json", ct);
    }
}
```

---

## 6. Unified Event Handling — IEventHandler<T>

```csharp
public interface IEventHandler<T>
{
    Task HandleAsync(CloudEventEnvelope<T> envelope, CancellationToken ct);
}
```

All domain or integration event consumers implement this interface.

---

## 7. Distributed Invoker Registry

A **startup-built registry** of precompiled delegates. No reflection during handling.

```csharp
public interface IDistributedEventInvoker
{
    string Name { get; }
    int Version { get; }
    Task InvokeAsync(IServiceProvider sp, ReadOnlyMemory<byte> body, CancellationToken ct);
}
```

### Invoker Implementation

```csharp
public sealed class DistributedEventInvoker<T> : IDistributedEventInvoker
{
    public async Task InvokeAsync(IServiceProvider sp, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var ser = sp.GetRequiredService<IEventSerializer>();
        var handler = sp.GetRequiredService<IEventHandler<T>>();
        var env = ser.Deserialize<T>(body.Span);
        await handler.HandleAsync(env, ct);
    }
}
```

### Registry

```csharp
public sealed class DistributedEventInvokerRegistry : IDistributedEventInvokerRegistry
{
    private readonly Dictionary<(string,int), IDistributedEventInvoker> _map = new();
}
```

---

## 8. Controller Integration (No MinimalAPI)

### Discovery Endpoint (Dapr Subscribe)

```csharp
[Route("dapr")]
public sealed class DaprDiscoveryController : ControllerBase
{
    [HttpGet("subscribe")]
    public IActionResult Get([FromServices] IDistributedEventInvokerRegistry reg) =>
        Ok(reg.All().Select(x => new { pubsubname = "pubsub", topic = x.Topic, route = $"/events/{x.Name}/v{x.Version}" }));
}
```

### Event Receiver Endpoint

```csharp
[Route("events/{name}/v{version:int}")]
public sealed class EventsController : ControllerBase
{
    private readonly IDistributedEventInvokerRegistry _reg;
    private readonly IServiceProvider _sp;

    [HttpPost]
    public async Task<IActionResult> Post(string name, int version, CancellationToken ct)
    {
        if (!_reg.TryGet(name, version, out var inv)) return NotFound();
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        await inv.InvokeAsync(_sp, ms.ToArray(), ct);
        return Ok();
    }
}
```

---

## 9. DI and Startup

```csharp
builder.Services.AddDaprDistributedEvents(
    typeof(EventsController).Assembly,
    typeof(OrderCreatedEventHandler).Assembly);
builder.Services.AddControllers();
```

---

## 10. Example Event Flow

1. `Order` aggregate adds `OrderCreatedDomainEvent`.
2. UoW commit collects domain events and publishes via `IDistributedEventBus`.
3. Dapr sidecar sends the event to subscribers.
4. `EventsController` routes the event by (name, version).
5. `InvokerRegistry` finds the correct handler delegate and executes it.

---

## 11. Advantages

### ✅ Architectural Advantages

1. **Zero runtime reflection** — all handlers resolved to delegates at startup.
2. **Strong typing** — enforced `IEventHandler<T>` with `CloudEventEnvelope<T>`.
3. **Unified path** — same `IDistributedEventBus` used by domain and integration events.
4. **UoW integration** — events can be published post-commit safely.
5. **Dapr-native** — automatic `/dapr/subscribe` discovery and routing.
6. **Extensible** — pluggable serializers, buses, and outbox implementations.
7. **High performance** — precompiled invocation path and async I/O.

### ⚙️ Operational Advantages

1. **Clean debugging** — explicit `name/version/topic` via `[EventName]`.
2. **Safe refactors** — static cache prevents runtime reflection surprises.
3. **Scalable** — multiple handlers and versions coexist.
4. **Resilient** — fits easily into outbox/inbox retry systems.
5. **Traceable** — each event envelope is CloudEvent-compliant.

---

## 12. Example: Handler

```csharp
public sealed class OrderCreatedEventHandler : IEventHandler<OrderCreatedDomainEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<OrderCreatedDomainEvent> env, CancellationToken ct)
    {
        var e = env.Data!;
        Console.WriteLine($"Handled order {e.OrderId} for {e.Amount}");
        await Task.CompletedTask;
    }
}
```

---

## 13. Integration Summary

| Layer | Responsibility |
|--------|----------------|
| **Aggregate** | Emits domain events (`AddDomainEvent`) |
| **UoW** | Collects and publishes via `IDistributedEventBus` |
| **Bus** | Serializes and publishes to Dapr pubsub |
| **Controller** | Receives and dispatches events to handlers |
| **InvokerRegistry** | Maps (name,version) to delegate |
| **Handler** | Business logic per event |

---

## 14. Outcome

This architecture offers an **enterprise-grade distributed eventing system** with:
- Full Dapr interoperability
- Minimal overhead
- Compile-time metadata validation
- Extensible serialization and bus layers
- Safe UoW event propagation

---

**Prepared for Architectural Review**  
*© 2025 — vNext Distributed Runtime (Burgan Tech)*
