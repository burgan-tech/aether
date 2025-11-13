using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.AspNetCore.Events;

/// <summary>
/// Abstract base class for event receiver endpoints for Dapr event delivery.
/// Routes events by name and version using precompiled invokers.
/// Developers should inherit from this class and create their own controller actions with custom routes.
/// </summary>
public abstract class EventsController(
    IDistributedEventInvokerRegistry invokerRegistry,
    IInboxStore inboxStore,
    IUnitOfWorkManager unitOfWorkManager,
    IServiceProvider serviceProvider,
    IEventSerializer serializer,
    ILogger<EventsController> logger) : ControllerBase
{
    protected readonly IDistributedEventInvokerRegistry InvokerRegistry = invokerRegistry;
    protected readonly IInboxStore InboxStore = inboxStore;
    protected readonly IUnitOfWorkManager UnitOfWorkManager = unitOfWorkManager;
    protected readonly IServiceProvider ServiceProvider = serviceProvider;
    protected readonly IEventSerializer Serializer = serializer;
    protected readonly ILogger<EventsController> Logger = logger;

    /// <summary>
    /// Processes incoming events from Dapr.
    /// Flow: Read body → Check inbox → Lookup invoker → Invoke handler → Mark as processed
    /// Developers should call this method from their controller action.
    /// </summary>
    /// <param name="name">Event name from route</param>
    /// <param name="version">Event version from route</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Action result indicating success or failure</returns>
    protected virtual async Task<IActionResult> ProcessEventAsync(
        string name,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            // Step 1: Read request body as bytes
            var payload = await ReadRequestBodyAsync(cancellationToken);

            // Step 2: Extract CloudEvent Id from JSON for idempotency check
            var eventId = TryExtractEventId(payload, name, version);
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return Ok(); // Return OK to prevent Dapr retries on malformed/missing ID
            }
            
            // Step 3: Open UoW for this event processing (handler + inbox)
            await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);

            // Step 4: Check inbox for duplicate (idempotency)
            if (await InboxStore.HasProcessedAsync(eventId, cancellationToken))
            {
                Logger.LogInformation("Event {EventId} ({Name} v{Version}) has already been processed, skipping", 
                    eventId, name, version);
                return Ok();
            }

            // Step 5: Lookup invoker from registry by (name, version)
            if (!InvokerRegistry.TryGet(name, version, out var invoker))
            {
                Logger.LogDebug("No handler registered for event {Name} v{Version}", name, version);
                return NotFound($"No handler registered for event '{name}' version {version}");
            }
            
            // Step 6: Invoke handler with precompiled invoker (no reflection!)
            await invoker.InvokeAsync(ServiceProvider, payload, cancellationToken);
            
            // Step 7: Mark as processed in inbox (only tracks, doesn't save)
            await TryMarkEventAsProcessedAsync(payload, eventId, cancellationToken);

            // Step 8: Commit UoW (flushes inbox + any changes made by handler)
            await uow.CommitAsync(cancellationToken);

            Logger.LogDebug("Successfully processed event {EventId} ({Name} v{Version})", eventId, name, version);
            return Ok();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling event {Name} v{Version}", name, version);
            
            // Return 500 to trigger Dapr retry mechanism
            return StatusCode(500, new { error = "Internal server error processing event" });
        }
    }

    /// <summary>
    /// Reads the request body as a byte array.
    /// Can be overridden to customize request body reading.
    /// </summary>
    protected virtual async Task<byte[]> ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        await using var memoryStream = new MemoryStream();
        await Request.Body.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Attempts to extract the CloudEvent ID from the payload.
    /// Returns null if extraction fails or ID is missing.
    /// Can be overridden to customize event ID extraction.
    /// </summary>
    protected virtual string? TryExtractEventId(byte[] payload, string name, int version)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(payload);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("id", out var idElement) || root.TryGetProperty("Id", out idElement))
            {
                return idElement.GetString();
            }

            Logger.LogWarning("CloudEvent Id is missing from event {Name} v{Version}", name, version);
            return null;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Failed to parse CloudEvent Id from event {Name} v{Version}", name, version);
            return null;
        }
    }

    /// <summary>
    /// Attempts to mark the event as processed in the inbox.
    /// Logs a warning if marking fails but does not throw.
    /// Can be overridden to customize inbox marking behavior.
    /// </summary>
    protected virtual async Task TryMarkEventAsProcessedAsync(byte[] payload, string eventId, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = Serializer.Deserialize<CloudEventEnvelope>(payload);
            
            if (envelope != null)
            {
                await InboxStore.MarkProcessedAsync(envelope, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to mark event {EventId} as processed in inbox", eventId);
        }
    }
}

