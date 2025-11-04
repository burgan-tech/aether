using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Marker interface for DbContext implementations that support the inbox pattern.
/// Implementing this interface indicates the DbContext provides an InboxMessages table.
/// </summary>
public interface IHasInbox
{
    /// <summary>
    /// Gets the DbSet for inbox messages.
    /// </summary>
    DbSet<InboxMessage> InboxMessages { get; }
}

