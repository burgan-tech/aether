using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Marker interface for DbContext implementations that support the outbox pattern.
/// Implementing this interface indicates the DbContext provides an OutboxMessages table.
/// </summary>
public interface IHasOutbox
{
    /// <summary>
    /// Gets the DbSet for outbox messages.
    /// </summary>
    DbSet<OutboxMessage> OutboxMessages { get; }
}

