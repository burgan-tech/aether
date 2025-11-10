using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Manages unit of work creation and ambient propagation.
/// Handles scope participation semantics (Required, RequiresNew, Suppress).
/// Supports prepare/initialize pattern for deferred UoW activation.
/// </summary>
public interface IUnitOfWorkManager
{
    /// <summary>
    /// Gets the current active unit of work, if any.
    /// Filters out prepared, completed, and disposed units of work.
    /// </summary>
    IUnitOfWork? Current { get; }

    /// <summary>
    /// Begins a new unit of work or participates in an existing one based on the options.
    /// </summary>
    /// <param name="options">Options controlling scope behavior and transaction settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A unit of work scope</returns>
    Task<IUnitOfWork> BeginAsync(UnitOfWorkOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares a unit of work without initializing it.
    /// Creates a placeholder that can be initialized later by aspects or services.
    /// </summary>
    /// <param name="preparationName">Name to identify this preparation</param>
    /// <param name="requiresNew">If true, always creates a new prepared UoW; otherwise reuses existing one with same name</param>
    /// <returns>A prepared unit of work scope</returns>
    IUnitOfWork Prepare(string preparationName, bool requiresNew = false);

    /// <summary>
    /// Attempts to find and initialize a prepared unit of work with the given name.
    /// Walks the outer chain looking for a matching prepared UoW.
    /// </summary>
    /// <param name="preparationName">Name of the prepared UoW to find</param>
    /// <param name="options">Options to initialize the UoW with</param>
    /// <param name="cancellationToken"></param>
    /// <returns>True if a prepared UoW was found and initialized; otherwise false</returns>
    Task<bool> TryBeginPreparedAsync(string preparationName, UnitOfWorkOptions options, CancellationToken cancellationToken = default);
}

