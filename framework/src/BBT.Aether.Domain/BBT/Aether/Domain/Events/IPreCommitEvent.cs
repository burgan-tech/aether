namespace BBT.Aether.Domain.Events;

/// <summary>
/// Marker interface for events that should be processed BEFORE SaveChanges.
/// Use this for events that need to be handled within the same transaction.
/// </summary>
public interface IPreCommitEvent : IDomainEvent
{
}
