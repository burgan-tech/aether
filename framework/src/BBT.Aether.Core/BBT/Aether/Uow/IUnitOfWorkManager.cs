using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Manages unit of work creation and ambient propagation.
/// Handles scope participation semantics (Required, RequiresNew, Suppress).
/// </summary>
public interface IUnitOfWorkManager
{
    /// <summary>
    /// Gets the current ambient unit of work, if any.
    /// </summary>
    IUnitOfWork? Current { get; }

    /// <summary>
    /// Begins a new unit of work or participates in an existing one based on the options.
    /// </summary>
    /// <param name="options">Options controlling scope behavior and transaction settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A unit of work scope</returns>
    Task<IUnitOfWork> BeginAsync(UnitOfWorkOptions? options = null, CancellationToken cancellationToken = default);
}

