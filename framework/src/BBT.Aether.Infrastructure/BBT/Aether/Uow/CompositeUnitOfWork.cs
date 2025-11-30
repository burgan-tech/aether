using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.Uow.EntityFrameworkCore;

namespace BBT.Aether.Uow;

/// <summary>
/// Coordinates transactions across multiple data sources.
/// Implements two-phase commit pattern: all sources commit or all rollback.
/// Supports deferred initialization: can be created without immediate initialization.
/// Dispatches domain events after successful commit of all sources.
/// </summary>
public sealed class CompositeUnitOfWork(
    IEnumerable<ILocalTransactionSource> sources,
    IDomainEventDispatcher? eventDispatcher = null,
    AetherDomainEventOptions? domainEventOptions = null)
    : IUnitOfWork, ITransactionalRoot, IUnitOfWorkEventEnqueuer
{
    private readonly List<(ILocalTransaction tx, ITransactionalLocal? tLocal)> _opened = new();
    private readonly List<Func<IUnitOfWork, Task>> _completedHandlers = new();
    private readonly List<Func<IUnitOfWork, Exception?, Task>> _failedHandlers = new();
    private readonly List<Action<IUnitOfWork>> _disposedHandlers = new();
    private bool _isInitialized;
    private Exception? _exception;

    /// <summary>
    /// Gets the unique identifier for this unit of work.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets whether this unit of work has been initialized with transaction sources.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Gets whether this unit of work has been aborted by a nested scope.
    /// </summary>
    public bool IsAborted { get; private set; }

    /// <summary>
    /// Gets whether this unit of work has been completed (committed or rolled back).
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public UnitOfWorkOptions? Options { get; private set; }

    /// <inheritdoc />
    public IUnitOfWork? Outer { get; private set; }

    /// <inheritdoc />
    public bool IsPrepared => false;

    /// <inheritdoc />
    public string? PreparationName => null;

    /// <summary>
    /// Initializes transactions for all registered sources.
    /// Can be called after construction to support deferred initialization.
    /// If options.IsTransactional is false, transactions can be escalated later.
    /// </summary>
    public async Task InitializeAsync(UnitOfWorkOptions options, CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("CompositeUnitOfWork has already been initialized.");
        }

        Options = options;

        foreach (var source in sources)
        {
            var transaction = await source.CreateTransactionAsync(options, cancellationToken);
            _opened.Add((transaction, transaction as ITransactionalLocal));
        }

        _isInitialized = true;
    }

    /// <inheritdoc />
    public void Prepare(string preparationName)
    {
        throw new NotSupportedException("CompositeUnitOfWork does not support prepare pattern. Use UnitOfWorkScope instead.");
    }

    /// <inheritdoc />
    public void Initialize(UnitOfWorkOptions options)
    {
        throw new NotSupportedException("Use InitializeAsync instead.");
    }

    /// <inheritdoc />
    public bool IsPreparedFor(string preparationName) => false;

    /// <inheritdoc />
    public void SetOuter(IUnitOfWork? outer)
    {
        Outer = outer;
    }

    /// <summary>
    /// Marks this unit of work as aborted, preventing commit.
    /// </summary>
    public void Abort()
    {
        IsAborted = true;
    }

    /// <inheritdoc />
    public void EnqueueEvent(DomainEventEnvelope eventEnvelope)
    {
        // Push events to all transactions that support enqueueing
        // Typically there's one EF Core transaction, but we support multiple
        foreach (var (tx, _) in _opened)
        {
            if (tx is ILocalTransactionEventEnqueuer enqueuer)
            {
                enqueuer.EnqueueEvents(new[] { eventEnvelope });
            }
        }
    }

    /// <summary>
    /// Ensures that all local transactions are escalated to transactional mode.
    /// This is used for lazy escalation when needed.
    /// </summary>
    public async Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return; // No-op if not initialized
        }

        foreach (var (_, tLocal) in _opened)
        {
            if (tLocal is not null)
            {
                await tLocal.EnsureTransactionAsync(isolationLevel, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Saves changes to all transaction sources that support explicit SaveChanges.
    /// No-op if not initialized.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return; // No-op if not initialized
        }

        // Call SaveChanges on all sources that support it
        foreach (var (tx, _) in _opened)
        {
            if (tx is ISupportsSaveChanges saveable)
            {
                await saveable.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Commits all transactions in order, then dispatches domain events.
    /// Throws if the unit of work has been aborted.
    /// No-op if not initialized.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return; // No-op if not initialized
        }

        if (IsAborted)
        {
            throw new InvalidOperationException(
                "Unit of work has been aborted by an inner scope and cannot be committed.");
        }

        try
        {
            var strategy = domainEventOptions?.DispatchStrategy ?? DomainEventDispatchStrategy.AlwaysUseOutbox;
            
            if (strategy == DomainEventDispatchStrategy.AlwaysUseOutbox)
            {
                await CommitWithOutboxAsync(cancellationToken);
            }
            else
            {
                await CommitWithDirectPublishAsync(cancellationToken);
            }
            
            IsCompleted = true;
            await InvokeCompletedHandlersAsync();
        }
        catch (Exception e)
        {
            _exception = e;
            throw;
        }
    }

    /// <summary>
    /// Commits using the AlwaysUseOutbox strategy.
    /// Writes events to outbox within the transaction before commit.
    /// </summary>
    private async Task CommitWithOutboxAsync(CancellationToken cancellationToken)
    {
        // Step 1: Collect all domain events from all transactions
        // Events are automatically pushed to transactions via IDomainEventSink during SaveChanges
        // Use HashSet to deduplicate events that may have been pushed to multiple transactions
        var allEvents = new HashSet<DomainEventEnvelope>();
        foreach (var (tx, _) in _opened)
        {
            foreach (var evt in tx.CollectedEvents)
            {
                allEvents.Add(evt);
            }
        }

        if (allEvents.Any() && eventDispatcher != null)
        {
            await eventDispatcher.DispatchEventsAsync(allEvents, cancellationToken);
            await SaveChangesAsync(cancellationToken);
            
            // Clear events from all transactions after successful dispatch
            foreach (var (tx, _) in _opened)
            {
                tx.ClearCollectedEvents();
            }
        }
        
        // Step 3: Commit all transactions
        foreach (var (tx, _) in _opened)
        {
            await tx.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Commits using the PublishWithFallback strategy.
    /// Commits first, then publishes directly. On failure, writes to outbox in new scope.
    /// </summary>
    private async Task CommitWithDirectPublishAsync(CancellationToken cancellationToken)
    {
        // Step 1: Collect all domain events from all transactions
        // Events are automatically pushed to transactions via IDomainEventSink during SaveChanges
        // Use HashSet to deduplicate events that may have been pushed to multiple transactions
        var allEvents = new HashSet<DomainEventEnvelope>();
        foreach (var (tx, _) in _opened)
        {
            foreach (var evt in tx.CollectedEvents)
            {
                allEvents.Add(evt);
            }
        }
        
        // Step 2: Commit all transactions (business data is now persisted)
        foreach (var (tx, _) in _opened)
        {
            await tx.CommitAsync(cancellationToken);
        }
        
        // Step 3: Publish events directly after commit
        if (allEvents.Any() && eventDispatcher != null)
        {
            try
            {
                await eventDispatcher.PublishDirectlyAsync(allEvents, cancellationToken);
                
                // Clear events from all transactions after successful publish
                foreach (var (tx, _) in _opened)
                {
                    tx.ClearCollectedEvents();
                }
            }
            catch (Exception ex)
            {
                // Business data is already committed, so we log and attempt fallback to outbox
                // This ensures business data is not lost even if publish fails
                try
                {
                    await eventDispatcher.WriteToOutboxInNewScopeAsync(allEvents, cancellationToken);
                    
                    // Clear events after successful outbox write
                    foreach (var (tx, _) in _opened)
                    {
                        tx.ClearCollectedEvents();
                    }
                }
                catch (Exception outboxEx)
                {
                    // Both publish and outbox fallback failed
                    // Business data is already committed, but events are lost
                    // This is a critical scenario that should be monitored
                    throw new AggregateException(
                        "Failed to publish events directly and failed to write to outbox as fallback. Business data was committed successfully, but events may be lost.",
                        ex, outboxEx);
                }
            }
        }
    }

    /// <summary>
    /// Rolls back all transactions in reverse order.
    /// Exceptions during rollback are caught and ignored to allow all sources to attempt rollback.
    /// No-op if not initialized.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return; // No-op if not initialized
        }

        // Rollback in reverse order
        for (var i = _opened.Count - 1; i >= 0; i--)
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

        // Invoke OnFailed handlers after rollback
        await InvokeFailedHandlersAsync();
    }

    /// <summary>
    /// Disposes the unit of work, rolling back if not completed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // If not completed or exception occurred, handle failure
        if (!IsCompleted || _exception != null)
        {
            if (!IsCompleted)
            {
                // Rollback will call InvokeFailedHandlersAsync
                await RollbackAsync();
            }
            else if (_exception != null)
            {
                // Exception during commit but some cleanup succeeded
                await InvokeFailedHandlersAsync();
            }
        }

        // Always invoke disposed handlers if initialized
        if (_isInitialized)
        {
            InvokeDisposedHandlers();
        }

        // Dispose all transaction sources
        foreach (var (tx, _) in _opened)
        {
            if (tx is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }

        IsDisposed = true;
    }

    /// <summary>
    /// Registers a handler to be invoked after successful commit.
    /// </summary>
    public IDisposable OnCompleted(Func<IUnitOfWork, Task> handler)
    {
        _completedHandlers.Add(handler);
        return new AetherSubscription<Func<IUnitOfWork, Task>>(_completedHandlers, handler);
    }

    /// <summary>
    /// Registers a handler to be invoked after rollback or failed commit.
    /// </summary>
    public IDisposable OnFailed(Func<IUnitOfWork, Exception?, Task> handler)
    {
        _failedHandlers.Add(handler);
        return new AetherSubscription<Func<IUnitOfWork, Exception?, Task>>(_failedHandlers, handler);
    }

    /// <summary>
    /// Registers a handler to be invoked during disposal.
    /// </summary>
    public IDisposable OnDisposed(Action<IUnitOfWork> handler)
    {
        _disposedHandlers.Add(handler);
        return new AetherSubscription<Action<IUnitOfWork>>(_disposedHandlers, handler);
    }

    private async Task InvokeCompletedHandlersAsync()
    {
        // Iterate over a copy to allow handlers to unsubscribe
        foreach (var handler in _completedHandlers.ToArray())
        {
            try
            {
                await handler(this);
            }
            catch
            {
                // Log error but don't throw - commit already succeeded
            }
        }
    }

    private async Task InvokeFailedHandlersAsync()
    {
        // Iterate over a copy to allow handlers to unsubscribe
        foreach (var handler in _failedHandlers.ToArray())
        {
            try
            {
                await handler(this, _exception);
            }
            catch
            {
                // Log error but don't throw - allow other handlers to run
            }
        }
    }

    private void InvokeDisposedHandlers()
    {
        // Iterate over a copy to allow handlers to unsubscribe
        foreach (var handler in _disposedHandlers.ToArray())
        {
            try
            {
                handler(this);
            }
            catch
            {
                // Log error but don't throw - allow other handlers to run
            }
        }
    }
}