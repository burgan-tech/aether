using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for DbContext classes that support the <c>worker_settings</c> table.
/// Implement this alongside <see cref="IHasEfCoreWorkerSlots"/> to enable runtime slot management
/// via the Admin API.
/// </summary>
public interface IHasEfCoreWorkerSettings
{
    /// <summary>Gets or sets the DbSet for worker settings entities.</summary>
    DbSet<WorkerSettings> WorkerSettings { get; set; }
}
