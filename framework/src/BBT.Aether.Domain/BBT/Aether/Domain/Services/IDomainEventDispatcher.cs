using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Defines a service for dispatching domain events.
/// </summary>
public interface IDomainEventDispatcher
{
    /// <summary>
    /// Dispatches a collection of domain event envelopes.
    /// Events are published via the distributed event bus using their metadata.
    /// If publishing fails and WriteToOutboxOnPublishError is enabled, events are stored in the outbox for retry.
    /// Used by AlwaysUseOutbox strategy.
    /// </summary>
    /// <param name="eventEnvelopes">The domain event envelopes to dispatch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DispatchEventsAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Publishes events directly to the broker without outbox fallback.
    /// Used by PublishWithFallback strategy after transaction commit.
    /// Throws exceptions on publish failure for the caller to handle.
    /// </summary>
    /// <param name="eventEnvelopes">The domain event envelopes to publish</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task PublishDirectlyAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Writes events to outbox in a new scope, isolated from the current transaction.
    /// Used by PublishWithFallback strategy when direct publish fails after commit.
    /// Creates a new scope and transaction to persist events to outbox.
    /// </summary>
    /// <param name="eventEnvelopes">The domain event envelopes to write to outbox</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteToOutboxInNewScopeAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes, CancellationToken cancellationToken = default);
}

