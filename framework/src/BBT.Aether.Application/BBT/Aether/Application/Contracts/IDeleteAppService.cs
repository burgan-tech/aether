using System.Threading.Tasks;

namespace BBT.Aether.Application;

/// <summary>
/// Defines a service for deleting entities.
/// </summary>
/// <typeparam name="TKey">The type of the entity's primary key.</typeparam>
public interface IDeleteAppService<in TKey> : IApplicationService
{
    /// <summary>
    /// Deletes an entity asynchronously.
    /// </summary>
    /// <param name="id">The ID of the entity to delete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DeleteAsync(TKey id);
}
