using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for EF Core DbContext implementations that support the outbox pattern.
/// Implementing this interface indicates the DbContext provides an OutboxMessages DbSet.
/// This interface is specific to EF Core and lives in the Infrastructure layer to maintain
/// clean architecture and keep the Domain layer persistence-ignorant.
/// </summary>
public interface IHasEfCoreOutbox
{
    /// <summary>
    /// Gets the DbSet for outbox messages.
    /// </summary>
    DbSet<OutboxMessage> OutboxMessages { get; }
}

