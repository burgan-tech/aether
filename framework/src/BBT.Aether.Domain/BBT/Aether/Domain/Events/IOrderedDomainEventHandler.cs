using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Defines an ordered handler for domain events.
/// Handlers implementing this interface will be executed in order based on their Order property.
/// </summary>
/// <typeparam name="TEvent">The type of domain event to handle.</typeparam>
public interface IOrderedDomainEventHandler<in TEvent> : IDomainEventHandler<TEvent> where TEvent : IDomainEvent
{
    /// <summary>
    /// Gets the execution order of this handler.
    /// Handlers with lower order values are executed first.
    /// Default order is 0.
    /// </summary>
    int Order { get; }
}
