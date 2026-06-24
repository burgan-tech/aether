using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a unit of work that coordinates changes across multiple transaction sources.
/// Supports commit, rollback, and automatic disposal with rollback semantics.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this unit of work.
    /// Used to track UoW identity across scopes and for debugging.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Gets the configuration options for this unit of work.
    /// </summary>
    UnitOfWorkOptions? Options { get; }

    /// <summary>
    /// Gets the outer (parent) unit of work in the scope chain, if any.
    /// </summary>
    IUnitOfWork? Outer { get; }

    /// <summary>
    /// Gets whether this unit of work has been aborted by a nested scope.
    /// </summary>
    bool IsAborted { get; }

    /// <summary>
    /// Gets whether this unit of work has been completed (committed or rolled back).
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Gets whether this unit of work has been disposed.
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Sets the outer (parent) unit of work in the scope chain.
    /// </summary>
    /// <param name="outer">The outer unit of work</param>
    void SetOuter(IUnitOfWork? outer);

    /// <summary>
    /// Saves changes to all transaction sources without committing.
    /// Useful for intermediate persistence before final commit.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all changes made within this unit of work.
    /// Throws if the unit of work has been aborted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back all changes made within this unit of work.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks this unit of work as aborted, preventing commit.
    /// </summary>
    void Abort();

    /// <summary>
    /// Registers a handler to be invoked after successful commit.
    /// The handler will execute after all transactions have been committed and events dispatched.
    /// If the handler throws an exception, it will not rollback the commit.
    /// </summary>
    /// <param name="handler">The async handler to invoke on successful completion, receives the UoW instance</param>
    /// <returns>A disposable subscription that can be used to unregister the handler</returns>
    IDisposable OnCompleted(Func<IUnitOfWork, Task> handler);

    /// <summary>
    /// Registers a handler to be invoked after rollback or failed commit.
    /// The handler will execute after rollback has completed.
    /// Exceptions in the handler are caught and logged but not propagated.
    /// </summary>
    /// <param name="handler">The async handler to invoke on failure, receives the UoW instance and exception (if any)</param>
    /// <returns>A disposable subscription that can be used to unregister the handler</returns>
    IDisposable OnFailed(Func<IUnitOfWork, Exception?, Task> handler);

    /// <summary>
    /// Registers a handler to be invoked during disposal.
    /// The handler will execute regardless of success or failure, but only if the UoW was initialized.
    /// This is useful for cleanup operations that should always run.
    /// </summary>
    /// <param name="handler">The sync handler to invoke on disposal, receives the UoW instance</param>
    /// <returns>A disposable subscription that can be used to unregister the handler</returns>
    IDisposable OnDisposed(Action<IUnitOfWork> handler);
}

