using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Events.Distributed;

/// <summary>
/// Defines a publisher for distributed domain events.
/// </summary>
public interface IDistributedDomainEventPublisher
{
    /// <summary>
    /// Publishes distributed domain events to external systems.
    /// </summary>
    /// <param name="events">The distributed events to publish.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PublishAsync(IEnumerable<IDistributedDomainEvent> events, CancellationToken cancellationToken = default);
}
