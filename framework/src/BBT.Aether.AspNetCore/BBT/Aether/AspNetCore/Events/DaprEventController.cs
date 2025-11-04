using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BBT.Aether.Events;

namespace BBT.Aether.AspNetCore.Events;

[ApiController]
[Route("api/dapr/events")]
public class DaprEventController(
    IServiceProvider serviceProvider,
    IEventSerializer serializer,
    IEventTypeRegistry eventTypeRegistry,
    ITopicNameStrategy topicNameStrategy,
    IInboxStore inboxStore,
    ILogger<DaprEventController> logger)
    : ControllerBase
{
    private readonly static JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Returns Dapr subscription configuration for all registered event handlers.
    /// This endpoint is called by Dapr runtime to discover subscriptions.
    /// Dapr expects this endpoint at /dapr/subscribe (absolute path, ignoring controller route).
    /// </summary>
    [HttpGet("/dapr/subscribe", Order = int.MinValue)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
    {
        var subscriptions = new List<DaprSubscription>();

        foreach (var descriptor in eventTypeRegistry.All)
        {
            // Get topic name using topic name strategy (includes environment prefix if enabled)
            var topicName = topicNameStrategy.GetTopicName(descriptor.ClrEventType);

            var subscription = new DaprSubscription
            {
                Topic = topicName,
                PubsubName = descriptor.PubSubName, // Use PubSubName from descriptor (resolved from attribute or default)
                Route = $"/api/dapr/events/{topicName}"
            };

            subscriptions.Add(subscription);
        }

        return new JsonResult(subscriptions, JsonOptions);
    }

    [HttpPost("{topicName}")]
    public async Task<IActionResult> HandleAsync([FromRoute] string topicName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Read request body as bytes
            await using var memoryStream = new System.IO.MemoryStream();
            await Request.Body.CopyToAsync(memoryStream, cancellationToken);
            var payload = memoryStream.ToArray();

            // Parse the final payload to extract Type and Id fields
            var jsonDoc = JsonDocument.Parse(payload);
            var root = jsonDoc.RootElement;

            // Extract Id property from JSON (CloudEvent Id field)
            string? eventId = null;
            if (root.TryGetProperty("id", out var idElement) || root.TryGetProperty("Id", out idElement))
            {
                eventId = idElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(eventId))
            {
                logger.LogWarning("CloudEventEnvelope.Id is missing or empty from topic {TopicName}", topicName);
                jsonDoc.Dispose();
                return Ok();
            }

            // Check if event has already been processed (idempotency check)
            if (await inboxStore.HasProcessedAsync(eventId, cancellationToken))
            {
                logger.LogInformation("Event {EventId} has already been processed, skipping", eventId);
                jsonDoc.Dispose();
                return Ok();
            }

            // Extract Type property from JSON (CloudEvent Type field)
            if (!root.TryGetProperty("type", out var typeElement) && !root.TryGetProperty("Type", out typeElement))
            {
                logger.LogWarning("CloudEventEnvelope.Type is missing from topic {TopicName}", topicName);
                jsonDoc.Dispose();
                return Ok();
            }

            var cloudEventType = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(cloudEventType))
            {
                logger.LogWarning("CloudEventEnvelope.Type is empty from topic {TopicName}", topicName);
                jsonDoc.Dispose();
                return Ok();
            }

            // Resolve CLR type from registry using CloudEvent Type field directly
            var clrType = eventTypeRegistry.Resolve(cloudEventType);
            if (clrType == null)
            {
                logger.LogDebug("No handler registered for CloudEvent Type={Type}, topic={TopicName}", cloudEventType,
                    topicName);
                jsonDoc.Dispose();
                return Ok();
            }

            // Deserialize directly to strongly-typed CloudEventEnvelope<TEvent>
            // This eliminates reflection for data extraction!
            var typedEnvelopeType = typeof(CloudEventEnvelope<>).MakeGenericType(clrType);
            var typedEnvelope = serializer.Deserialize(payload, typedEnvelopeType);

            if (typedEnvelope == null)
            {
                logger.LogWarning("Failed to deserialize CloudEventEnvelope<{EventType}> from topic {TopicName}",
                    clrType.Name, topicName);
                jsonDoc.Dispose();
                return Ok();
            }

            // Resolve handlers from DI
            var handlerInterface = typeof(IDistributedEventHandler<>).MakeGenericType(clrType);
            var handlers = serviceProvider.GetServices(handlerInterface);

            var enumerable = handlers as object?[] ?? handlers.ToArray();
            if (!enumerable.Any())
            {
                logger.LogDebug("No handlers found for event type {EventType}", clrType.Name);
                return Ok();
            }

            // Invoke handlers with strongly-typed envelope
            // Handlers receive CloudEventEnvelope<TEvent> with type-safe data access
            var handleAsyncMethod = handlerInterface.GetMethod(nameof(IDistributedEventHandler<object>.HandleAsync))!;
            var tasks = enumerable
                .Select(handler =>
                    (Task)handleAsyncMethod.Invoke(handler, [typedEnvelope, cancellationToken])!)
                .ToList();

            await Task.WhenAll(tasks);

            // Mark event as processed after successful handling
            // Cast to non-generic for storage
            await inboxStore.MarkProcessedAsync((CloudEventEnvelope)typedEnvelope, cancellationToken);

            jsonDoc.Dispose();
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Dapr event from topic {TopicName}", topicName);
            return StatusCode(500);
        }
    }
}