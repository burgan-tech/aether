using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// A null object implementation of IDomainEventDispatcher that does nothing.
/// Useful for design-time contexts, migrations, or testing scenarios.
/// </summary>
public sealed class NullDomainEventDispatcher : IDomainEventDispatcher
{
    public static readonly NullDomainEventDispatcher Instance = new();

    private NullDomainEventDispatcher() { }

    public Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask; // Do nothing
    }
}