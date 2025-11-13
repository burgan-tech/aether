using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for EF Core DbContext implementations that support the inbox pattern.
/// Implementing this interface indicates the DbContext provides an InboxMessages DbSet.
/// This interface is specific to EF Core and lives in the Infrastructure layer to maintain
/// clean architecture and keep the Domain layer persistence-ignorant.
/// </summary>
public interface IHasEfCoreInbox
{
    /// <summary>
    /// Gets the DbSet for inbox messages.
    /// </summary>
    DbSet<InboxMessage> InboxMessages { get; }
}

