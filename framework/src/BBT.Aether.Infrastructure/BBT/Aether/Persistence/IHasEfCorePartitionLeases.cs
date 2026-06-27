using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for DbContext classes that support the partition lease table used to assign
/// logical partition ownership to inbox and outbox workers.
/// </summary>
public interface IHasEfCorePartitionLeases
{
    /// <summary>Gets or sets the DbSet for partition lease entities.</summary>
    DbSet<PartitionLease> PartitionLeases { get; set; }
}
