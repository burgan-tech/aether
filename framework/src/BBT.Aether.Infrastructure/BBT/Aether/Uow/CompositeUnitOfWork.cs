using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.Uow.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Uow;

/// <summary>
/// Root unit of work backed by a single shared <see cref="DbConnection"/> and a single
/// <see cref="DbTransaction"/>. Hands out lazily-created schema-bound <see cref="DbContext"/>
/// instances keyed by (DbContextType, Schema). Each created context enlists on the shared
/// transaction via <c>UseTransactionAsync</c> and is bound to its schema by the configured
/// database provider, so schema isolation is a provider concern.
/// Dispatches domain events after successful commit, preserving the outbox / direct-publish pipeline.
/// </summary>
public sealed class CompositeUnitOfWork(
    IServiceProvider serviceProvider,
    IDomainEventDispatcher? eventDispatcher = null,
    AetherDomainEventOptions? domainEventOptions = null)
    : IEfCoreUnitOfWork, ITransactionalRoot
{
    private readonly Dictionary<DbContextKey, DbContext> _contexts = new();
    private readonly List<DomainEventEnvelope> _events = new();
    private readonly List<Func<IUnitOfWork, Task>> _completedHandlers = new();
    private readonly List<Func<IUnitOfWork, Exception?, Task>> _failedHandlers = new();
    private readonly List<Action<IUnitOfWork>> _disposedHandlers = new();

    private readonly SchemaScopeState _schemaState = new();

    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private UnitOfWorkOptions _options = new();
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _failedHandlersInvoked;
    private Exception? _exception;

    /// <summary>
    /// Gets the unique identifier for this unit of work.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets whether this unit of work has been initialized.
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
    public bool IsDisposed => _isDisposed;

    /// <inheritdoc />
    public UnitOfWorkOptions? Options { get; private set; }

    /// <inheritdoc />
    public IUnitOfWork? Outer { get; private set; }

    /// <summary>
    /// Initializes the unit of work. Does NOT open the connection here — the connection and
    /// transaction are opened lazily on the first <see cref="GetDbContextAsync{TDbContext}"/>
    /// call, so an empty unit of work costs nothing.
    /// </summary>
    public Task InitializeAsync(UnitOfWorkOptions options, CancellationToken cancellationToken = default)
    {
        InitializeCore(options);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synchronously initializes the unit of work. Because initialization does no real async work
    /// (it only sets fields; the connection/transaction open lazily on first DbContext creation),
    /// this lets a caller begin a unit of work in its own execution frame — which is required for
    /// ambient (AsyncLocal) propagation to flow into the caller's continuations.
    /// </summary>
    public void InitializeCore(UnitOfWorkOptions options)
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("CompositeUnitOfWork has already been initialized.");
        }

        _options = options;
        Options = options;
        _isInitialized = true;
    }

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
    public async Task<TDbContext> GetDbContextAsync<TDbContext>(string schema, CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        if (_isDisposed || IsCompleted)
        {
            throw new InvalidOperationException("Cannot get a DbContext from a completed or disposed unit of work.");
        }

        var key = new DbContextKey(typeof(TDbContext), schema);
        if (_contexts.TryGetValue(key, out var existing))
        {
            return (TDbContext)existing;
        }

        if (_contexts.Count >= _options.MaxDbContextCount)
        {
            throw new InvalidOperationException(
                $"UnitOfWork DbContext limit exceeded. Limit: {_options.MaxDbContextCount}");
        }

        var configurator = serviceProvider.GetRequiredService<IAetherDbContextConfigurator<TDbContext>>();

        if (_connection is null)
        {
            _connection = configurator.CreateConnection();
            await _connection.OpenAsync(cancellationToken);

            // Reset schema state whenever a fresh connection is established.
            _schemaState.Current = null;

            if (_options.IsTransactional || configurator.RequiresTransaction)
            {
                _transaction = await _connection.BeginTransactionAsync(
                    _options.IsolationLevel ?? IsolationLevel.ReadCommitted, cancellationToken);
            }
        }

        var options = configurator.BuildOptions(_connection, schema, _schemaState);
        var context = ActivatorUtilities.CreateInstance<TDbContext>(serviceProvider, options);

        if (_transaction is not null)
        {
            await context.Database.UseTransactionAsync(_transaction, cancellationToken);
        }

        if (context is AetherDbContext<TDbContext> aether)
        {
            aether.LocalEventEnqueuer = new BufferEnqueuer(_events);
        }

        _contexts[key] = context;
        return context;
    }

    /// <summary>
    /// Ensures that the shared transaction is started. In the new model the transaction is
    /// always opened together with the connection on first DbContext creation, so this is a
    /// no-op once a context has been created. No-op if not initialized.
    /// </summary>
    public Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        // The connection and transaction are opened lazily and together on the first
        // GetDbContextAsync call. There is nothing to escalate here.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves changes on every materialized context that has pending changes.
    /// No-op if not initialized.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return;
        }

        foreach (var context in _contexts.Values)
        {
            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Commits the shared transaction, then dispatches domain events.
    /// Throws if the unit of work has been aborted. No-op if not initialized.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized || IsCompleted)
        {
            return;
        }

        if (IsAborted)
        {
            throw new InvalidOperationException(
                "Unit of work has been aborted by an inner scope and cannot be committed.");
        }

        try
        {
            await SaveChangesAsync(cancellationToken);

            // There may be no connection/transaction if nothing was read or written.
            if (_transaction is not null)
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
    /// Writes events to the outbox within the shared transaction before commit.
    /// </summary>
    private async Task CommitWithOutboxAsync(CancellationToken cancellationToken)
    {
        if (_events.Any() && eventDispatcher != null)
        {
            await eventDispatcher.DispatchEventsAsync(_events, cancellationToken);

            // Persist outbox rows written by the dispatcher into the shared transaction.
            await SaveChangesAsync(cancellationToken);

            _events.Clear();
        }

        await _transaction!.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Commits using the PublishWithFallback strategy.
    /// Commits first, then publishes directly. On failure, writes to outbox in a new scope.
    /// </summary>
    private async Task CommitWithDirectPublishAsync(CancellationToken cancellationToken)
    {
        // Snapshot events before commit; the contexts will be committed and cleared.
        var allEvents = _events.ToList();

        // Step 1: Commit the shared transaction (business data is now persisted).
        await _transaction!.CommitAsync(cancellationToken);

        // Step 2: Publish events directly after commit.
        if (allEvents.Any() && eventDispatcher != null)
        {
            try
            {
                await eventDispatcher.PublishDirectlyAsync(allEvents, cancellationToken);

                _events.Clear();
            }
            catch (Exception ex)
            {
                // Business data is already committed, so we attempt fallback to outbox
                // in a new scope. This ensures business data is not lost even if publish fails.
                try
                {
                    await eventDispatcher.WriteToOutboxInNewScopeAsync(allEvents, cancellationToken);

                    _events.Clear();
                }
                catch (Exception outboxEx)
                {
                    // Both publish and outbox fallback failed.
                    // Business data is already committed, but events are lost.
                    // This is a critical scenario that should be monitored.
                    throw new AggregateException(
                        "Failed to publish events directly and failed to write to outbox as fallback. Business data was committed successfully, but events may be lost.",
                        ex, outboxEx);
                }
            }
        }
    }

    /// <summary>
    /// Rolls back the shared transaction. Exceptions during rollback are swallowed.
    /// No-op if not initialized.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // No-op if never initialized or already completed (committed or rolled back). Mirrors CommitAsync.
        if (!_isInitialized || IsCompleted)
        {
            return;
        }

        if (_transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
            catch
            {
                // Ignore rollback errors.
            }
        }

        IsCompleted = true;

        await InvokeFailedHandlersAsync();
    }

    /// <summary>
    /// Disposes the unit of work, rolling back if not completed, then disposing all
    /// materialized contexts, the transaction, and the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        // Set early: disposal must be a one-shot even if a step below throws, so a retry never
        // re-invokes failed/disposed handlers or double-releases resources.
        _isDisposed = true;

        if (!IsCompleted)
        {
            // Rollback will call InvokeFailedHandlersAsync.
            await RollbackAsync();
        }
        else if (_exception != null)
        {
            await InvokeFailedHandlersAsync();
        }

        if (_isInitialized)
        {
            InvokeDisposedHandlers();
        }

        // Dispose every resource, but never let one failure prevent releasing the connection —
        // the connection is the pooled resource whose leak we must avoid. Collect and surface
        // the first failure after everything has been attempted.
        Exception? firstFailure = null;

        // For SessionSearchPath mode the provider registers a cleanup that resets the session-level
        // search_path before the connection is returned to the pool.
        if (_schemaState.Cleanup is not null && _schemaState.Current is not null && _connection is not null)
        {
            try
            {
                await _schemaState.Cleanup(_connection, CancellationToken.None);
                _schemaState.Current = null;
            }
            catch
            {
                // Swallow — a cleanup failure must not prevent connection disposal.
            }
        }

        foreach (var context in _contexts.Values)
        {
            try
            {
                await context.DisposeAsync();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (_transaction is not null)
        {
            try
            {
                await _transaction.DisposeAsync();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
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
        // Fire at most once: both RollbackAsync and the DisposeAsync `_exception != null` branch can reach
        // here (e.g. commit throws → explicit RollbackAsync → DisposeAsync), and handlers must not run twice.
        if (_failedHandlersInvoked)
        {
            return;
        }

        _failedHandlersInvoked = true;

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

    /// <summary>
    /// Routes events collected by a DbContext during SaveChanges into the unit of work's
    /// shared event buffer, deduplicating by reference.
    /// </summary>
    private sealed class BufferEnqueuer(List<DomainEventEnvelope> buffer) : ILocalTransactionEventEnqueuer
    {
        public void EnqueueEvents(IEnumerable<DomainEventEnvelope> events)
        {
            foreach (var evt in events)
            {
                if (!buffer.Contains(evt))
                {
                    buffer.Add(evt);
                }
            }
        }
    }
}
