using System.Threading.Tasks;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Defines a service for seeding initial data into the system.
/// </summary>
public interface IDataSeedService
{
    /// <summary>
    /// Seeds initial data into the system using the provided context.
    /// </summary>
    /// <param name="context">The context for the seeding operation.</param>
    Task SeedAsync(SeedContext context);
}