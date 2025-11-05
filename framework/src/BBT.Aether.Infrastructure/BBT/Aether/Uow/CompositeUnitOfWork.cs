using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;

namespace BBT.Aether.Uow;

/// <summary>
/// Coordinates transactions across multiple data sources.
/// Implements two-phase commit pattern: all sources commit or all rollback.
/// Supports lazy transaction escalation from reserve (non-transactional) to transactional mode.
/// Dispatches domain events after successful commit of all sources.
/// </summary>
public sealed class CompositeUnitOfWork(
    IEnumerable<ILocalTransactionSource> sources,
    IDomainEventDispatcher? eventDispatcher = null)
    : IAsyncDisposable, ITransactionalRoot
{
    private readonly List<(ILocalTransaction tx, ITransactionalLocal? tLocal)> _opened = new();

    /// <summary>
    /// Gets the unique identifier for this unit of work.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets whether this unit of work has been aborted by a nested scope.
    /// </summary>
    public bool IsAborted { get; private set; }

    /// <summary>
    /// Gets whether this unit of work has been completed (committed or rolled back).
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <summary>
    /// Initializes transactions for all registered sources.
    /// If options.IsTransactional is false, transactions are reserved (lazy initialization).
    /// </summary>
    public async Task InitializeAsync(UnitOfWorkOptions options, CancellationToken cancellationToken = default)
    {
        foreach (var source in sources)
        {
            var transaction = await source.CreateTransactionAsync(options, cancellationToken);
            _opened.Add((transaction, transaction as ITransactionalLocal));
        }
    }

    /// <summary>
    /// Marks this unit of work as aborted, preventing commit.
    /// </summary>
    public void Abort()
    {
        IsAborted = true;
    }

    /// <summary>
    /// Ensures that all local transactions are escalated to transactional mode.
    /// This is used for lazy escalation: middleware reserves without transaction,
    /// aspects/services escalate when needed.
    /// </summary>
    public async Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default)
    {
        foreach (var (_, tLocal) in _opened)
        {
            if (tLocal is not null)
            {
                await tLocal.EnsureTransactionAsync(isolationLevel, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Commits all transactions in order, then dispatches domain events.
    /// Throws if the unit of work has been aborted.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (IsAborted)
        {
            throw new InvalidOperationException("Unit of work has been aborted by an inner scope and cannot be committed.");
        }

        // Step 1: Commit all transactions
        foreach (var (tx, _) in _opened)
        {
            await tx.CommitAsync(cancellationToken);
        }

        // Step 2: Collect all domain events from all transactions
        var allEvents = new List<DomainEventEnvelope>();
        foreach (var (tx, _) in _opened)
        {
            allEvents.AddRange(tx.CollectedEvents);
        }

        // Step 3: Dispatch domain events after successful commit
        if (allEvents.Any() && eventDispatcher != null)
        {
            await eventDispatcher.DispatchEventsAsync(allEvents, cancellationToken);

            // Step 4: Clear events from all transactions after successful dispatch
            foreach (var (tx, _) in _opened)
            {
                tx.ClearCollectedEvents();
            }
        }

        IsCompleted = true;
    }

    /// <summary>
    /// Rolls back all transactions in reverse order.
    /// Exceptions during rollback are caught and ignored to allow all sources to attempt rollback.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // Rollback in reverse order
        for (int i = _opened.Count - 1; i >= 0; i--)
        {
            try
            {
                await _opened[i].tx.RollbackAsync(cancellationToken);
            }
            catch
            {
                // Ignore rollback errors to allow all sources to rollback
            }
        }

        IsCompleted = true;
    }

    /// <summary>
    /// Disposes the unit of work, rolling back if not completed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsCompleted)
        {
            await RollbackAsync();
        }
    }
}

