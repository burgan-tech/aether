namespace BBT.Aether.Domain.Events;

/// <summary>
/// Marker interface for events that should be processed AFTER SaveChanges.
/// Use this for events that represent side effects after data is persisted.
/// This is the recommended approach for most domain events.
/// </summary>
public interface IPostCommitEvent : IDomainEvent
{
}
