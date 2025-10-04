using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Events.Distributed;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Domain.EntityFrameworkCore;

/// <summary>
/// Provides transaction management for Entity Framework Core DbContext.
/// Handles database transactions with proper error handling and logging.
/// </summary>
public sealed class EfCoreTransactionService<TDbContext>(
    TDbContext dbContext, 
    ILogger<EfCoreTransactionService<TDbContext>> logger,
    IEventContext? eventContext = null)
    : ITransactionService, ISupportsRollback, ITransactionEventStorage
    where TDbContext : DbContext
{
    private IDbContextTransaction? _currentTransaction;
    private bool _disposed;
    private readonly List<IPostCommitEvent> _postCommitEvents = new();
    private readonly List<IDistributedDomainEvent> _distributedEvents = new();

    public bool HasActiveTransaction => _currentTransaction != null;
    public bool HasStoredEvents => _postCommitEvents.Count > 0 || _distributedEvents.Count > 0;

    public void StorePostCommitEvents(IEnumerable<IPostCommitEvent> events)
    {
        if (events?.Any() == true)
        {
            _postCommitEvents.AddRange(events);
            logger.LogDebug("Stored {Count} post-commit events for later dispatch", events.Count());
        }
    }

    public void StoreDistributedEvents(IEnumerable<IDistributedDomainEvent> events)
    {
        if (events?.Any() == true)
        {
            _distributedEvents.AddRange(events);
            logger.LogDebug("Stored {Count} distributed events for later dispatch", events.Count());
        }
    }

    public async Task BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfTransactionActive();

        logger.LogDebug("Beginning new transaction with isolation level: {IsolationLevel}", isolationLevel);
        try
        {
            _currentTransaction =
                await dbContext.Database.BeginTransactionAsync(isolationLevel, cancellationToken: cancellationToken);
            logger.LogDebug("Transaction successfully started");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to begin transaction with isolation level: {IsolationLevel}", isolationLevel);
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNoTransaction();

        try
        {
            // First commit the database transaction
            await dbContext.SaveChangesAsync(cancellationToken);
            await _currentTransaction!.CommitAsync(cancellationToken);
            logger.LogDebug("Transaction successfully committed");

            // After successful commit, dispatch stored events
            await DispatchStoredEvents(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit transaction. Initiating rollback");
            await RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            await DisposeTransactionAsync();
            ClearStoredEvents();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNoTransaction();

        logger.LogDebug("Rolling back transaction");
        try
        {
            await _currentTransaction!.RollbackAsync(cancellationToken);
            logger.LogDebug("Transaction successfully rolled back");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rollback transaction");
            throw;
        }
        finally
        {
            await DisposeTransactionAsync();
            ClearStoredEvents(); // Clear stored events on rollback
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        logger.LogDebug("Disposing transaction service");
        if (_currentTransaction != null)
        {
            _currentTransaction.Dispose();
            _currentTransaction = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        logger.LogDebug("Disposing transaction service asynchronously");
        if (_currentTransaction != null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            logger.LogError("Attempted to use disposed transaction service");
            throw new ObjectDisposedException(nameof(EfCoreTransactionService<TDbContext>));
        }
    }

    private void ThrowIfTransactionActive()
    {
        if (HasActiveTransaction)
        {
            logger.LogError("Attempted to begin a transaction while another is in progress");
            throw new InvalidOperationException("A transaction is already in progress");
        }
    }

    private void ThrowIfNoTransaction()
    {
        if (!HasActiveTransaction)
        {
            logger.LogError("Attempted to perform operation with no active transaction");
            throw new InvalidOperationException("No active transaction");
        }
    }

    private async ValueTask DisposeTransactionAsync()
    {
        if (_currentTransaction != null)
        {
            logger.LogDebug("Disposing current transaction");
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    private async Task DispatchStoredEvents(CancellationToken cancellationToken)
    {
        if (eventContext == null)
        {
            logger.LogWarning("No event context available. Stored events will not be dispatched");
            return;
        }

        try
        {
            // Dispatch post-commit events
            if (_postCommitEvents.Count > 0)
            {
                logger.LogDebug("Dispatching {Count} post-commit events after successful transaction commit", _postCommitEvents.Count);
                await eventContext.DispatchPostCommitEventsAsync(_postCommitEvents, cancellationToken);
            }

            // Publish distributed events
            if (_distributedEvents.Count > 0)
            {
                logger.LogDebug("Publishing {Count} distributed events after successful transaction commit", _distributedEvents.Count);
                await eventContext.PublishDistributedEventsAsync(_distributedEvents, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch events after successful transaction commit. Database changes are already committed.");
            // Note: We don't rethrow here because the database transaction was already committed successfully
            // Event dispatch failures should be handled separately (e.g., retry mechanisms, dead letter queues)
        }
    }

    private void ClearStoredEvents()
    {
        if (_postCommitEvents.Count > 0 || _distributedEvents.Count > 0)
        {
            logger.LogDebug("Clearing {PostCommitCount} post-commit events and {DistributedCount} distributed events", 
                _postCommitEvents.Count, _distributedEvents.Count);
            _postCommitEvents.Clear();
            _distributedEvents.Clear();
        }
    }
}