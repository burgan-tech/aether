using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a unit of work that coordinates changes across multiple transaction sources.
/// Supports prepare/initialize pattern, commit, rollback, and automatic disposal with rollback semantics.
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
    /// Null if the UoW is prepared but not yet initialized.
    /// </summary>
    UnitOfWorkOptions? Options { get; }

    /// <summary>
    /// Gets the outer (parent) unit of work in the scope chain, if any.
    /// </summary>
    IUnitOfWork? Outer { get; }

    /// <summary>
    /// Gets whether this unit of work is in prepared state (not yet initialized).
    /// Prepared UoWs act as placeholders and don't perform actual work until initialized.
    /// </summary>
    bool IsPrepared { get; }

    /// <summary>
    /// Gets the preparation name that identifies this prepared unit of work.
    /// Used to match and initialize prepared UoWs from aspects or services.
    /// </summary>
    string? PreparationName { get; }

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
    /// Prepares this unit of work without initializing it.
    /// Creates a placeholder that can be initialized later with specific options.
    /// </summary>
    /// <param name="preparationName">Name to identify this preparation</param>
    void Prepare(string preparationName);

    /// <summary>
    /// Initializes a prepared unit of work with specific options.
    /// Converts a prepared placeholder into an active unit of work.
    /// </summary>
    /// <param name="options">Options to configure the unit of work</param>
    void Initialize(UnitOfWorkOptions options);

    /// <summary>
    /// Checks if this unit of work was prepared with a specific name.
    /// </summary>
    /// <param name="preparationName">Name to check</param>
    /// <returns>True if prepared with the given name</returns>
    bool IsPreparedFor(string preparationName);

    /// <summary>
    /// Sets the outer (parent) unit of work in the scope chain.
    /// </summary>
    /// <param name="outer">The outer unit of work</param>
    void SetOuter(IUnitOfWork? outer);

    /// <summary>
    /// Saves changes to all transaction sources without committing.
    /// Useful for intermediate persistence before final commit.
    /// No-op if the unit of work is still in prepared state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all changes made within this unit of work.
    /// Throws if the unit of work has been aborted.
    /// No-op if the unit of work is still in prepared state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back all changes made within this unit of work.
    /// No-op if the unit of work is still in prepared state.
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

