using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the inbox store, used for event idempotency checking.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Checks if an event with the specified ID has already been processed.
    /// </summary>
    /// <param name="eventId">The unique event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the event has been processed, false otherwise</returns>
    Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an event as processed by storing the event id.
    /// </summary>
    /// <param name="eventId">The unique event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stores a new event as pending for background processing.
    /// </summary>
    /// <param name="envelope">The CloudEventEnvelope to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a batch of pending events ready for processing.
    /// </summary>
    /// <param name="batchSize">Maximum number of events to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of inbox messages with Pending status</returns>
    Task<List<InboxMessage>> GetPendingEventsAsync(int batchSize, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Marks an event as currently being processed.
    /// </summary>
    /// <param name="eventId">The unique event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkAsProcessingAsync(string eventId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Marks an event as failed/discarded.
    /// </summary>
    /// <param name="eventId">The unique event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Cleans up old processed messages.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to cleanup</param>
    /// <param name="retentionPeriod">Retention period for processed messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of messages deleted</returns>
    Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}

