using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Entity Framework Core implementation of ILocalTransactionSource.
/// Creates and manages database transactions within a unit of work.
/// Collects domain events before commit for UoW-level dispatching.
/// </summary>
public sealed class EfCoreTransactionSource<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider) : ILocalTransactionSource
    where TDbContext : AetherDbContext<TDbContext>
{
    /// <inheritdoc />
    public string SourceName => $"efcore:{typeof(TDbContext).Name}";

    /// <inheritdoc />
    public async Task<ILocalTransaction> CreateTransactionAsync(
        UnitOfWorkOptions options,
        CancellationToken cancellationToken = default)
    {
        var dbContext = dbContextProvider.GetDbContext();
        
        IDbContextTransaction? transaction = null;

        if (options.IsTransactional)
        {
            transaction = options.IsolationLevel.HasValue 
            ? await dbContext.Database.BeginTransactionAsync(
                options.IsolationLevel.Value,
                cancellationToken)
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        return new EfCoreLocalTransaction(dbContext, transaction);
    }

    /// <summary>
    /// Local transaction implementation for EF Core.
    /// Supports lazy transaction escalation and domain event collection.
    /// Events are automatically pushed via IDomainEventSink during SaveChanges.
    /// </summary>
    private sealed class EfCoreLocalTransaction(
        AetherDbContext<TDbContext> context,
        IDbContextTransaction? transaction)
        : ILocalTransaction, ITransactionalLocal, ISupportsSaveChanges, IAsyncDisposable, ILocalTransactionEventEnqueuer
    {
        private IDbContextTransaction? _transaction = transaction;
        private readonly List<DomainEventEnvelope> _collectedEvents = new();

        /// <inheritdoc />
        public IReadOnlyList<DomainEventEnvelope> CollectedEvents => _collectedEvents;

        /// <summary>
        /// Ensures that a transaction is started for this local transaction if not already started.
        /// Implements lazy transaction escalation pattern.
        /// </summary>
        public async Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default)
        {
            // If transaction already exists, no-op
            if (_transaction != null)
            {
                return;
            }

            // Begin transaction with specified or default isolation level
            _transaction = isolationLevel.HasValue
                ? await context.Database.BeginTransactionAsync(isolationLevel.Value, cancellationToken)
                : await context.Database.BeginTransactionAsync(cancellationToken);
        }

        /// <inheritdoc />
        public void EnqueueEvents(IEnumerable<DomainEventEnvelope> events)
        {
            // Push pattern: events are pushed from DbContext via sink
            foreach (var evt in events)
            {
                if (!_collectedEvents.Contains(evt))
                {
                    _collectedEvents.Add(evt);
                }
            }
        }

        /// <inheritdoc />
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            // Commit the transaction if present
            if (_transaction != null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }
        }

        /// <inheritdoc />
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
        }

        /// <inheritdoc />
        public void ClearCollectedEvents()
        {
            context.ClearDomainEvents();
            _collectedEvents.Clear();
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_transaction != null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }
    }
}

/// <summary>
/// Internal interface for local transactions that support event enqueueing.
/// Allows the sink to push events into transactions.
/// </summary>
internal interface ILocalTransactionEventEnqueuer
{
    /// <summary>
    /// Enqueues events that were collected by DbContext during SaveChanges.
    /// </summary>
    /// <param name="events">The events to enqueue</param>
    void EnqueueEvents(IEnumerable<DomainEventEnvelope> events);
}
