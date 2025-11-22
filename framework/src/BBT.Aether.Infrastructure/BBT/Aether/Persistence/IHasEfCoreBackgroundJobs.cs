using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Persistence;

/// <summary>
/// Marker interface for DbContext classes that support the Background Job pattern.
/// DbContext implementations must include a DbSet for BackgroundJobInfo entities.
/// </summary>
/// <remarks>
/// This interface maintains clean architecture principles by keeping the Domain layer
/// persistence-ignorant while allowing the Infrastructure layer to provide EF Core
/// specific implementations.
/// </remarks>
public interface IHasEfCoreBackgroundJobs
{
    /// <summary>
    /// Gets or sets the DbSet for background job entities.
    /// </summary>
    DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }
}

