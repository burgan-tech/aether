using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Interface for applying EF Core migrations across multiple schemas.
/// Developers must provide their own implementation to define which schemas to migrate.
/// </summary>
/// <typeparam name="TContext">The DbContext type.</typeparam>
public interface IMultiSchemaMigrator<TContext> where TContext : DbContext
{
    /// <summary>
    /// Applies migrations to a specific schema.
    /// </summary>
    /// <param name="schema">The schema to migrate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MigrateSchemaAsync(string schema, CancellationToken cancellationToken = default);
}

