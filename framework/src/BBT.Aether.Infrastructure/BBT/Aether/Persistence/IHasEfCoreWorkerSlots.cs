using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for DbContext classes that support the worker slot table used to limit
/// the number of active background job executors in the cluster.
/// </summary>
public interface IHasEfCoreWorkerSlots
{
    /// <summary>Gets or sets the DbSet for worker slot entities.</summary>
    DbSet<WorkerSlot> WorkerSlots { get; set; }
}
