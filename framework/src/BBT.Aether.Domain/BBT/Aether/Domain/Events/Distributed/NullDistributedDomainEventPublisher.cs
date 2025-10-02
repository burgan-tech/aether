using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Events.Distributed;

/// <summary>
/// A null object implementation of <see cref="IDistributedDomainEventPublisher"/> that does nothing.
/// This is useful for scenarios where distributed events are not needed, such as design-time contexts,
/// migrations, or testing scenarios.
/// </summary>
public sealed class NullDistributedDomainEventPublisher : IDistributedDomainEventPublisher
{
    /// <summary>
    /// Gets the singleton instance of the null distributed domain event publisher.
    /// </summary>
    public static readonly NullDistributedDomainEventPublisher Instance = new();

    private NullDistributedDomainEventPublisher()
    {
    }

    /// <inheritdoc />
    public Task PublishAsync(IEnumerable<IDistributedDomainEvent> events, CancellationToken cancellationToken = default)
    {
        // Do nothing - this is a null object implementation
        return Task.CompletedTask;
    }
}
