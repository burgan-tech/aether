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
    /// </summary>
    /// <param name="eventEnvelopes">The domain event envelopes to dispatch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DispatchEventsAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes, CancellationToken cancellationToken = default);
}

