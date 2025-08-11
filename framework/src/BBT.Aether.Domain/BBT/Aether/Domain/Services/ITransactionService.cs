using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Defines a service for managing database transactions.
/// </summary>
public interface ITransactionService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Begins a new database transaction with the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction. Defaults to ReadCommitted.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default);
    /// <summary>
    /// Commits the current transaction and saves all changes to the database.
    /// Automatically rolls back if any error occurs during the commit process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CommitAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Rolls back the current transaction, undoing all changes made within the transaction scope.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets a value indicating whether there is an active transaction.
    /// </summary>
    bool HasActiveTransaction { get; }
}