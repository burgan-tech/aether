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
    /// Gets the current active unit of work, if any.
    /// Filters out completed and disposed units of work.
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
    /// Synchronously begins a new unit of work (or participates in an existing one) and makes it the
    /// ambient unit of work in the CALLER's execution flow.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="BeginAsync"/>, whose ambient (AsyncLocal) assignment happens inside an async
    /// state machine and therefore does NOT propagate back to the caller's execution context, this
    /// synchronous method establishes the scope in the caller's own frame. The ambient assignment then
    /// flows into subsequent continuations, so provider-backed stores and repositories that resolve
    /// their DbContext via the ambient unit of work see the active UoW.
    /// <para>
    /// Prefer <c>Begin</c> for programmatic / background use (background jobs, inbox/outbox processors,
    /// dispatchers) where the unit of work must be ambient for subsequent provider/repository calls in
    /// the same method. Initialization does no real async work, so nothing is lost by going synchronous.
    /// </para>
    /// </remarks>
    /// <param name="options">Options controlling scope behavior and transaction settings</param>
    /// <returns>A unit of work scope that is ambient in the caller's flow</returns>
    IUnitOfWork Begin(UnitOfWorkOptions? options = null);
}

