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
    /// Gets whether this unit of work has been aborted by a nested scope.
    /// </summary>
    bool IsAborted { get; }

    /// <summary>
    /// Gets whether this unit of work has been completed (committed or rolled back).
    /// </summary>
    bool IsCompleted { get; }

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
}

