# Event Bus Usage

The Aether Event Bus provides distributed (cross-service) eventing with Dapr-based publishing using CloudEvents 1.0 specification. Events are automatically wrapped in **CloudEventEnvelope** format. Handlers are discovered via DI and event types are registered automatically.

## Registration

```csharp
// Register event bus with Dapr support - all in one call
services.AddAetherEventBus(options => 
{ 
    options.PrefixEnvironmentToTopic = true;
    options.DefaultSource = "urn:domain:order-service"; // Required: Source for CloudEventEnvelope
    options.PubSubName = "my-pubsub"; // Default Dapr PubSub component name
});
```

**What happens during registration:**
- Core event bus services are registered (serializer, topic strategy, etc.)
- All event handlers implementing `IDistributedEventHandler<>` are automatically discovered and registered
- Event subscription descriptors are built immediately based on handler attributes
- Event type registry is created and populated
- DaprEventBus is registered as the distributed event bus implementation

**No middleware required!** Everything is configured during service registration - no additional application startup calls needed.

## Event Definition

Events should be decorated with `EventNameAttribute`. You can optionally specify a `pubSubName` parameter to route events to specific Dapr PubSub components:

```csharp
// Basic event (uses default PubSub component from options)
[EventName("OrderCreated", version: 1)]
public sealed record OrderCreatedEvent(
    string OrderId,
    string CustomerId,
    DateTimeOffset OccurredOn
) : IDistributedEvent;

// Event routed to specific PubSub component (e.g., Kafka for single-consumer)
[EventName("OrderShipped", version: 1, pubSubName: "kafka-pubsub")]
public sealed record OrderShippedEvent(
    string OrderId,
    DateTimeOffset ShippedAt
) : IDistributedEvent;

// Event routed to broadcast PubSub (e.g., Redis for fan-out)
[EventName("NotificationSent", version: 1, pubSubName: "redis-pubsub")]
public sealed record NotificationSentEvent(
    string UserId,
    string Message
) : IDistributedEvent;
```

## Publishing

Events are published by passing the event payload directly. The EventBus automatically wraps the payload in a `CloudEventEnvelope` following CloudEvents 1.0 specification. The PubSub component is determined by the `pubSubName` parameter in the `EventNameAttribute` on the event type.

```csharp
// Publishing events - PubSub is determined by the pubSubName parameter in EventNameAttribute
var orderEvent = new OrderCreatedEvent 
    { 
        OrderId = orderId,
        CustomerId = customerId,
        OccurredOn = DateTimeOffset.UtcNow
};

// Publishes to default PubSub (no pubSubName specified in EventNameAttribute)
await distributedEventBus.PublishAsync(
    orderEvent,
    subject: orderId.ToString(),  // Optional: aggregate ID
    useOutbox: true);

// Publishes to kafka-pubsub (from pubSubName: "kafka-pubsub" in EventNameAttribute)
var shippedEvent = new OrderShippedEvent { OrderId = orderId, ShippedAt = DateTimeOffset.UtcNow };
await distributedEventBus.PublishAsync(shippedEvent, useOutbox: true);

// Publishes to redis-pubsub (from pubSubName: "redis-pubsub" in EventNameAttribute)
var notificationEvent = new NotificationSentEvent { UserId = userId, Message = "Order shipped!" };
await distributedEventBus.PublishAsync(notificationEvent);
```

**Automatic CloudEventEnvelope Creation:**
- `Type`: Automatically generated from `EventNameAttribute` on the event type using `TopicNameStrategy`
  - Format: `EventName.v{version}` or `{environment}.EventName.v{version}` if environment prefix is enabled
- `Source`: Automatically populated from `AetherEventBusOptions.DefaultSource` (required configuration)
- `Subject`: Optional parameter in `PublishAsync` (e.g., aggregate ID)
- `Data`: The event payload passed to `PublishAsync`
- `Id`, `Time`, `SpecVersion`, `DataContentType`: Auto-generated with defaults

## Topic Strategy

Topic name is constructed from event attributes and registry:
- Format: `domain.EventName.v{version}` (e.g., `order.OrderCreated.v1`)
- If environment prefix is enabled: `{environmentName}.domain.EventName.v{version}`
- Domain comes from `EventSubscriptionAttribute` on the handler class

## Handlers (auto-registered via DI)

Handlers must implement `IDistributedEventHandler<TEvent>` and use `EventSubscriptionAttribute` to specify the subscription. You can optionally specify a `pubSubName` parameter to subscribe to a specific Dapr PubSub component:

```csharp
// Handler for event using default PubSub
[EventSubscription("OrderCreated", version: 1)]
public sealed class OrderCreatedHandler : IDistributedEventHandler<OrderCreatedEvent>
{
    public Task HandleAsync(CloudEventEnvelope<OrderCreatedEvent> envelope, CancellationToken cancellationToken)
    {
        var orderId = envelope.Data.OrderId;
        var subject = envelope.Subject; // aggregateId
        // Handle event...
        return Task.CompletedTask;
    }
}

// Handler for event using specific PubSub component
[EventSubscription("OrderShipped", version: 1, pubSubName: "kafka-pubsub")]
public sealed class OrderShippedHandler : IDistributedEventHandler<OrderShippedEvent>
{
    public Task HandleAsync(CloudEventEnvelope<OrderShippedEvent> envelope, CancellationToken cancellationToken)
    {
        // Handle event...
        return Task.CompletedTask;
    }
}
```

**Note:** The `EventSubscriptionAttribute` specifies:
- `eventName`: Must match `EventNameAttribute.Name` on the event class
- `version`: Must match `EventNameAttribute.Version` on the event class
- `pubSubName` (optional): Overrides the default PubSub component for this subscription

## Dapr Subscription Configuration

The SDK automatically provides a `/dapr/subscribe` endpoint that Dapr calls to discover subscriptions. **No manual YAML subscription files are required** - subscriptions are automatically generated based on registered handlers.

### Automatic Subscription Discovery

When Dapr starts your application, it calls `GET /dapr/subscribe` to discover which topics your application subscribes to. The endpoint returns a JSON array of subscription configurations:

```json
[
  {
    "topic": "order.OrderCreated.v1",
    "pubsubname": "my-pubsub",
    "route": "/api/dapr/events/development.order.created.v1"
  }
]
```

The subscription is automatically generated from:
- **Topic**: Constructed using `ITopicNameStrategy` (includes environment prefix if enabled)
- **PubsubName**: From `EventSubscriptionAttribute.PubSubName` or `AetherEventBusOptions.PubSubName` (default)
- **Route**: Fixed pattern `/api/dapr/events/{topicName}` matching the event handler route

### Manual Subscription YAML Files (Optional)

For advanced scenarios (custom routing rules, dead letter topics, etc.), you can still create subscription YAML files:

```yaml
# subscriptions.yaml (place in components directory)
apiVersion: dapr.io/v2alpha1
kind: Subscription
metadata:
  name: order-created-subscription
spec:
  pubsubname: my-pubsub  # Must match AetherEventBusOptions.PubSubName or EventSubscriptionAttribute.PubSubName
  topic: order.order.created.v1
  routes:
    rules:
      - match: ""
        path: /api/dapr/events/order.order.created.v1
  scopes:
    - order-service
```

**Note**: If you use manual YAML subscriptions, ensure the routes match the automatic subscription format.

## Subscription Endpoint

The SDK exposes two controller endpoints:

### 1. Subscription Discovery Endpoint
- Route: `GET /dapr/subscribe`
- Purpose: Returns subscription configuration to Dapr runtime
- Returns: JSON array of `DaprSubscription` objects
- **Automatically generated** from registered handlers via `IEventTypeRegistry.All`

### 2. Event Handler Endpoint
- Route: `POST /api/dapr/events/{topicName}`
- `topicName` is the topic name (e.g., `order.created.v1` or `development.order.created.v1` with environment prefix)

The controller **only processes CloudEventEnvelope format**:
1. Deserializes incoming payload to CloudEventEnvelope<object>
2. Validates CloudEventEnvelope format (returns Ok if invalid, logs warning)
3. Validates and parses the `Type` field (`domain.EventName.v{version}`)
4. Resolves the CLR type from `IEventTypeRegistry`
5. Deserializes to typed CloudEventEnvelope<T>
6. Invokes all registered handlers for that event type

**Note**: Messages that are not in CloudEventEnvelope format are silently ignored (returns Ok with warning log). Custom subscription formats must be handled by developers in their own controllers.

## Configuration Options

### AetherEventBusOptions
- `PrefixEnvironmentToTopic`: If `true`, prefixes topic names with environment name (default: `true`)
- `DefaultSource`: **Required** Source value for CloudEventEnvelope (format: `"urn:vnext:{service}"`). Must be configured when setting up the EventBus.
- `PubSubName`: The name of the default Dapr PubSub component (default: `"pubsub"`). Can be overridden per event/subscription using the `pubSubName` parameter in `EventNameAttribute` or `EventSubscriptionAttribute`.

## Multi-PubSub Support

The EventBus supports routing different events to different Dapr PubSub components using the `pubSubName` parameter in `EventNameAttribute` or `EventSubscriptionAttribute`. This is useful when you need different message delivery semantics (e.g., broadcast vs single-consumer).

### PubSub Resolution

When publishing an event, the PubSub component is determined by:

1. **Event attribute**: `pubSubName` parameter in `EventNameAttribute` on the event class (if specified)
2. **Default configuration**: `AetherEventBusOptions.PubSubName` (if not specified)

### Subscription Behavior

- Handlers can specify which PubSub component to subscribe to using the `pubSubName` parameter in `EventSubscriptionAttribute`
- If not specified, handlers subscribe to the default PubSub component from `AetherEventBusOptions`
- The `/dapr/subscribe` endpoint returns the correct PubSubName for each subscription

### Example Dapr Configuration

```yaml
# Dapr components/kafka-pubsub.yaml (single-consumer)
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: kafka-pubsub
spec:
  type: pubsub.kafka
  version: v1
  metadata:
    - name: brokers
      value: "localhost:9092"
    - name: consumerGroup
      value: "order-service"

# Dapr components/redis-pubsub.yaml (broadcast)
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: redis-pubsub
spec:
  type: pubsub.redis
  version: v1
  metadata:
    - name: redisHost
      value: "localhost:6379"
```

### Use Cases

**When to use multiple PubSub components:**
- Different message delivery patterns (broadcast vs single-consumer)
- Different scalability/reliability requirements per event type
- Gradual migration between broker technologies
- Cost optimization (different brokers for different workloads)

**Examples:**
- Use Kafka (`kafka-pubsub`) for order events that need exactly-once processing with consumer groups
- Use Redis (`redis-pubsub`) for notification events that should fan-out to all service instances
- Use RabbitMQ (`rabbitmq-pubsub`) for integration events with competing consumers

### Complete Example

```csharp
// Event definitions with PubSub routing
[EventName("OrderCreated", version: 1, pubSubName: "kafka-pubsub")]  // Single-consumer: only one instance processes
public record OrderCreatedEvent(string OrderId, string CustomerId) : IDistributedEvent;

[EventName("CacheInvalidated", version: 1, pubSubName: "redis-pubsub")]  // Broadcast: all instances receive the event
public record CacheInvalidatedEvent(string CacheKey) : IDistributedEvent;

[EventName("UserLoggedIn", version: 1)]  // No pubSubName: uses default PubSub
public record UserLoggedInEvent(string UserId) : IDistributedEvent;

// Publishing - PubSub is automatically resolved from EventNameAttribute
await eventBus.PublishAsync(new OrderCreatedEvent(orderId, customerId));      // → kafka-pubsub
await eventBus.PublishAsync(new CacheInvalidatedEvent("users"));              // → redis-pubsub
await eventBus.PublishAsync(new UserLoggedInEvent(userId));                   // → default pubsub
```

## Notes

- **Automatic envelope creation**: Events are published by passing the payload directly - the EventBus automatically wraps it in CloudEventEnvelope format (CloudEvents 1.0)
- **DefaultSource is required**: Must be configured in `AetherEventBusOptions.DefaultSource` when setting up the EventBus
- **EventNameAttribute required**: Event types must be decorated with `EventNameAttribute` for Type generation
- **Multi-PubSub support**: Use `pubSubName` parameter in `EventNameAttribute` or `EventSubscriptionAttribute` to route events to different PubSub components. If not specified, uses the default from `AetherEventBusOptions.PubSubName`
- **No middleware required**: Everything is configured during `AddAetherEventBus()` - no additional application startup calls needed
- Handlers must be idempotent (at-least-once delivery)
- Use outbox pattern for transactional guarantees where needed
- **Automatic subscriptions**: No manual configuration needed - just create handlers with `[EventSubscription]` attributes. Handlers automatically subscribe to the correct PubSub component based on the `pubSubName` parameter
- **Manual subscriptions**: If using YAML files, ensure routes match the automatic subscription format (`/api/dapr/events/{topicName}`) and PubSubName matches the subscription configuration
- **Custom subscription formats**: If developers create custom subscriptions with non-CloudEventEnvelope formats, they must handle those in their own controllers - the SDK only processes CloudEventEnvelope-compliant messages
