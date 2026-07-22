using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Uow;

/// <summary>
/// Root unit of work handing out lazily-created schema-bound <see cref="DbContext"/> instances
/// keyed by (DbContextType, Schema). When <see cref="UnitOfWorkOptions.IsTransactional"/> is
/// <see langword="true"/>, the root opens a single shared <see cref="DbConnection"/> and
/// <see cref="DbTransaction"/> on first context creation and every context enlists via
/// <c>UseTransactionAsync</c>. When it is <see langword="false"/>, the root never opens a
/// connection itself: contexts are bound to the connection string and EF Core owns the
/// connection lifecycle (a pooled connection is rented per operation and returned immediately).
/// Schema binding is a provider concern in both shapes.
/// Domain events remain buffered until <see cref="CommitAsync"/>. Transactional roots preserve
/// the outbox / direct-publish commit ordering; non-transactional roots dispatch during
/// <see cref="CommitAsync"/>, without atomicity between auto-committed business writes and event
/// delivery.
/// </summary>
public sealed class CompositeUnitOfWork(
    IServiceProvider serviceProvider,
    IDomainEventDispatcher? eventDispatcher = null,
    AetherDomainEventOptions? domainEventOptions = null)
    : IEfCoreUnitOfWork, ITransactionalRoot
{
    private readonly Dictionary<DbContextKey, DbContext> _contexts = new();
    private readonly List<PendingDomainEvent> _events = new();
    private readonly List<Func<IUnitOfWork, Task>> _completedHandlers = new();
    private readonly List<Func<IUnitOfWork, Exception?, Task>> _failedHandlers = new();
    private readonly List<Action<IUnitOfWork>> _disposedHandlers = new();

    private readonly SchemaScopeState _schemaState = new();
    private readonly ICurrentSchema? _currentSchema = serviceProvider.GetService<ICurrentSchema>();

    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private UnitOfWorkOptions _options = new();
    private bool _effectiveIsTransactional;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _failedHandlersInvoked;
    private bool _transactionCommitted;
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

    /// <summary>
    /// Gets the transaction mode captured when this root was initialized. Later mutations of the
    /// caller-owned options object cannot change the root's transaction semantics.
    /// </summary>
    internal bool EffectiveIsTransactional => _effectiveIsTransactional;

    /// <inheritdoc />
    public UnitOfWorkOptions? Options { get; private set; }

    /// <inheritdoc />
    public IUnitOfWork? Outer { get; private set; }

    /// <summary>
    /// Initializes the unit of work. Does NOT open a connection here. A transactional root opens
    /// its shared connection and transaction lazily on the first
    /// <see cref="GetDbContextAsync{TDbContext}"/> call; a non-transactional root never opens one
    /// (EF Core rents pooled connections per operation), so an empty unit of work costs nothing.
    /// </summary>
    public Task InitializeAsync(UnitOfWorkOptions options, CancellationToken cancellationToken = default)
    {
        InitializeCore(options);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synchronously initializes the unit of work. Because initialization does no real async work
    /// (it only sets fields; the connection and optional transaction open lazily on first
    /// DbContext creation),
    /// this lets a caller begin a unit of work in its own execution frame — which is required for
    /// ambient (AsyncLocal) propagation to flow into the caller's continuations.
    /// </summary>
    public void InitializeCore(UnitOfWorkOptions options)
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("CompositeUnitOfWork has already been initialized.");
        }

        _effectiveIsTransactional = options.IsTransactional;
        _options = options;
        Options = options;
        _isInitialized = true;
    }

    /// <inheritdoc />
    public void SetOuter(IUnitOfWork? outer)
    {
        ThrowIfTerminal("change the outer scope of");
        UnitOfWorkOuterChainGuard.Validate(this, outer);
        Outer = outer;
    }

    /// <summary>
    /// Marks this unit of work as aborted, preventing commit.
    /// </summary>
    public void Abort()
    {
        if (IsCompleted || _isDisposed)
        {
            return;
        }

        IsAborted = true;
    }

    /// <summary>
    /// Gets or creates the context bound to <paramref name="schema"/>. For a transactional root,
    /// the first context opens the shared connection and the shared transaction, and every
    /// context enlists on them. A non-transactional root never opens a connection itself: its
    /// contexts are bound to the connection string, so EF Core rents a pooled connection per
    /// operation and returns it immediately — the unit of work holds no physical connection.
    /// </summary>
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

        // if (_contexts.Count >= _options.MaxDbContextCount)
        // {
        //     throw new InvalidOperationException(
        //         $"UnitOfWork DbContext limit exceeded. Limit: {_options.MaxDbContextCount}");
        // }

        var configurator = serviceProvider.GetRequiredService<IAetherDbContextConfigurator<TDbContext>>();

        DbContextOptions<TDbContext> options;
        if (_effectiveIsTransactional)
        {
            if (_connection is null)
            {
                _connection = configurator.CreateConnection();
                await _connection.OpenAsync(cancellationToken);

                // Reset schema state whenever a fresh connection is established.
                _schemaState.Current = null;

                _transaction = await _connection.BeginTransactionAsync(
                    _options.IsolationLevel ?? IsolationLevel.ReadCommitted, cancellationToken);
            }

            options = configurator.BuildOptions(_connection, schema, _schemaState);
        }
        else
        {
            options = configurator.BuildOwnedOptions(schema);
        }

        var context = ActivatorUtilities.CreateInstance<TDbContext>(serviceProvider, options);

        if (_transaction is not null)
        {
            await context.Database.UseTransactionAsync(_transaction, cancellationToken);
        }

        if (context is AetherDbContext<TDbContext> aether)
        {
            aether.LocalEventEnqueuer = new BufferEnqueuer(schema, _events);
        }

        _contexts[key] = context;
        return context;
    }

    /// <summary>
    /// Preserves the compatibility contract for callers that request a transaction. Transactional
    /// roots open their shared transaction with the connection on first DbContext creation;
    /// non-transactional roots are not escalated. This method is therefore a no-op.
    /// </summary>
    public Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        // A transactional root opens its transaction lazily with the connection. A
        // non-transactional root deliberately has nothing to escalate here.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves changes on every materialized context that has pending changes.
    /// No-op if not initialized.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminal("save changes in");

        if (!_isInitialized)
        {
            return;
        }

        foreach (var (key, context) in _contexts)
        {
            if (!context.ChangeTracker.HasChanges()) continue;

            using (CurrentSchema.Change(key.Schema))
                await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Completes this root using its configured transaction and event-dispatch strategy. A
    /// transactional root commits its shared transaction with the required outbox/direct-publish
    /// ordering. A non-transactional root dispatches buffered events only from this method, after
    /// pending business changes have auto-committed; those writes and event delivery are not
    /// atomic. Throws if the unit of work has been aborted. No-op if not initialized.
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
            // A PublishWithFallback retry can enter here after the physical database transaction
            // committed but delivery and its fallback both failed. At that point CommitAsync is a
            // delivery retry only: the completed transaction must never be saved/committed again.
            if (!_transactionCommitted)
                await SaveChangesAsync(cancellationToken);

            if (_events.Count > 0 && eventDispatcher is null)
            {
                throw new InvalidOperationException(
                    "Cannot commit pending domain events because IDomainEventDispatcher is not registered.");
            }

            var strategy = domainEventOptions?.DispatchStrategy ?? DomainEventDispatchStrategy.AlwaysUseOutbox;

            // There may be no connection/transaction if nothing was read or written.
            if (_transactionCommitted)
            {
                await PublishWithFallbackAsync(cancellationToken);
            }
            else if (_transaction is not null)
            {
                if (strategy == DomainEventDispatchStrategy.AlwaysUseOutbox)
                {
                    await CommitWithOutboxAsync(cancellationToken);
                }
                else
                {
                    await CommitWithDirectPublishAsync(cancellationToken);
                }
            }
            else if (_events.Count > 0)
            {
                if (strategy == DomainEventDispatchStrategy.AlwaysUseOutbox)
                {
                    await CommitWithoutTransactionAsync(cancellationToken);
                }
                else
                {
                    await PublishWithFallbackAsync(cancellationToken);
                }
            }

            _exception = null;
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
        if (_events.Count > 0)
        {
            await StageAndSaveOutboxEventsAsync(cancellationToken);
        }

        await _transaction!.CommitAsync(cancellationToken);
        _events.Clear();
    }

    /// <summary>
    /// Dispatches buffered domain events for a non-transactional unit of work (no shared
    /// transaction was opened).
    /// <para>
    /// The business data has already been durably persisted by the earlier (auto-save) writes, so
    /// there is nothing to co-commit here: the events are dispatched with the configured
    /// <see cref="AetherDomainEventOptions.DispatchStrategy"/> (writing outbox rows under
    /// AlwaysUseOutbox) and any resulting rows are persisted by a final <see cref="SaveChangesAsync"/>.
    /// Delivery is at-least-once but NOT atomic with the business writes — a crash between the two
    /// relies on the consumer's idempotent retry/recovery.
    /// </para>
    /// </summary>
    private async Task CommitWithoutTransactionAsync(CancellationToken cancellationToken)
    {
        // Auto-commit makes each contiguous schema run independently durable. Remove a run from
        // the retry buffer immediately after its outbox rows save successfully; if a later run
        // fails, retry resumes there without duplicating already-durable earlier runs and still
        // preserves A1,B1,A2 ordering.
        var allRuns = GetEventRuns();
        var processedEventCount = 0;
        try
        {
            foreach (var run in allRuns)
            {
                await StageAndSaveEventRunAsync(run, cancellationToken);
                processedEventCount += run.Events.Count;
            }
            _events.Clear();
        }
        catch
        {
            if (processedEventCount > 0)
            {
                _events.RemoveRange(0, processedEventCount);
            }
            throw;
        }
    }

    private async Task StageAndSaveEventRunAsync(
        (string Schema, List<PendingDomainEvent> Events) run,
        CancellationToken cancellationToken)
    {
        var trackedEntityStates = CaptureTrackedEntityStates();
        try
        {
            using (CurrentSchema.Change(run.Schema))
                await eventDispatcher!.DispatchEventsAsync(
                    run.Events.Select(x => x.Envelope).ToList(), cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DetachNewOutboxStagingEntities(trackedEntityStates);
            throw;
        }
    }

    private async Task StageAndSaveOutboxEventsAsync(CancellationToken cancellationToken)
    {
        var trackedEntityStates = CaptureTrackedEntityStates();
        try
        {
            await ForEachEventRunAsync(eventDispatcher!.DispatchEventsAsync, cancellationToken);

            // Persist outbox rows written by the dispatcher. In a transactional UoW this remains
            // part of the shared transaction; without one, the context save auto-commits.
            await SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DetachNewOutboxStagingEntities(trackedEntityStates);
            throw;
        }
    }

    private Dictionary<DbContext, List<(object Entity, EntityState State)>> CaptureTrackedEntityStates() =>
        _contexts.Values.ToDictionary(
            context => context,
            context => context.ChangeTracker.Entries()
                .Select(entry => (entry.Entity, entry.State))
                .ToList());

    private static void DetachNewOutboxStagingEntities(
        IReadOnlyDictionary<DbContext, List<(object Entity, EntityState State)>> trackedEntityStates)
    {
        foreach (var (context, previousEntries) in trackedEntityStates)
        {
            var newOutboxEntries = context.ChangeTracker.Entries()
                .Where(entry =>
                    entry.State == EntityState.Added &&
                    entry.Entity is Domain.Events.OutboxMessage &&
                    previousEntries.All(previous => !ReferenceEquals(previous.Entity, entry.Entity)))
                .ToList();

            foreach (var entry in newOutboxEntries)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Commits using the PublishWithFallback strategy.
    /// Commits first, then publishes directly. On failure, writes to outbox in a new scope.
    /// </summary>
    private async Task CommitWithDirectPublishAsync(CancellationToken cancellationToken)
    {
        // Step 1: Commit the shared transaction (business data is now persisted).
        await _transaction!.CommitAsync(cancellationToken);
        _transactionCommitted = true;

        // Step 2: Publish events directly after commit.
        await PublishWithFallbackAsync(cancellationToken);
    }

    private async Task PublishWithFallbackAsync(CancellationToken cancellationToken)
    {
        foreach (var run in GetEventRuns())
        {
            using var schemaScope = CurrentSchema.Change(run.Schema);
            try
            {
                await eventDispatcher!.PublishDirectlyAsync(
                    run.Events.Select(x => x.Envelope).ToList(), cancellationToken);
            }
            catch (Exception ex)
            {
                // Business data is already committed, so we attempt fallback to outbox
                // in a new scope. This ensures business data is not lost even if publish fails.
                try
                {
                    await eventDispatcher!.WriteToOutboxInNewScopeAsync(
                        run.Schema,
                        run.Events.Select(x => x.Envelope).ToList(), cancellationToken);
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

            foreach (var pendingEvent in run.Events)
                _events.Remove(pendingEvent);
        }
    }

    private async Task ForEachEventRunAsync(
        Func<IReadOnlyList<DomainEventEnvelope>, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        foreach (var run in GetEventRuns())
        {
            using (CurrentSchema.Change(run.Schema))
                await action(run.Events.Select(x => x.Envelope).ToList(), cancellationToken);
        }
    }

    private List<(string Schema, List<PendingDomainEvent> Events)> GetEventRuns()
    {
        var runs = new List<(string Schema, List<PendingDomainEvent> Events)>();
        foreach (var pendingEvent in _events)
        {
            if (runs.Count == 0 ||
                !string.Equals(runs[^1].Schema, pendingEvent.Schema, StringComparison.Ordinal))
            {
                runs.Add((pendingEvent.Schema, new List<PendingDomainEvent>()));
            }

            runs[^1].Events.Add(pendingEvent);
        }

        return runs;
    }

    private ICurrentSchema CurrentSchema => _currentSchema
        ?? throw new InvalidOperationException(
            "Cannot save or dispatch schema-bound data because ICurrentSchema is not registered.");

    /// <summary>
    /// Rolls back the shared transaction when one exists. A non-transactional root has no database
    /// transaction to roll back. Exceptions during rollback are swallowed. No-op if not initialized.
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
    /// Disposes the unit of work, rolling back an existing transaction if not completed, then
    /// disposing all materialized contexts, the optional transaction, and the connection.
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

        // A provider that wrote session-level state on the shared connection registers a cleanup
        // to reset it before the connection is returned to the pool.
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
    /// Registers a handler to be invoked after the unit of work completes successfully.
    /// </summary>
    public IDisposable OnCompleted(Func<IUnitOfWork, Task> handler)
    {
        ThrowIfCannotRegisterHandler();
        _completedHandlers.Add(handler);
        return new AetherSubscription<Func<IUnitOfWork, Task>>(_completedHandlers, handler);
    }

    /// <summary>
    /// Registers a handler to be invoked after rollback or failed commit.
    /// </summary>
    public IDisposable OnFailed(Func<IUnitOfWork, Exception?, Task> handler)
    {
        ThrowIfCannotRegisterHandler();
        _failedHandlers.Add(handler);
        return new AetherSubscription<Func<IUnitOfWork, Exception?, Task>>(_failedHandlers, handler);
    }

    /// <summary>
    /// Registers a handler to be invoked during disposal.
    /// </summary>
    public IDisposable OnDisposed(Action<IUnitOfWork> handler)
    {
        ThrowIfCannotRegisterHandler();
        _disposedHandlers.Add(handler);
        return new AetherSubscription<Action<IUnitOfWork>>(_disposedHandlers, handler);
    }

    private void ThrowIfCannotRegisterHandler()
    {
        if (IsCompleted || _isDisposed)
        {
            throw new InvalidOperationException(
                "Cannot register handlers on a completed or disposed unit of work.");
        }
    }

    private void ThrowIfTerminal(string operation)
    {
        if (IsCompleted || _isDisposed)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} a completed or disposed unit of work.");
        }
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
    private sealed class BufferEnqueuer(string schema, List<PendingDomainEvent> buffer)
        : ILocalTransactionEventEnqueuer
    {
        public void EnqueueEvents(IEnumerable<DomainEventEnvelope> events)
        {
            foreach (var evt in events)
            {
                if (buffer.All(x => !ReferenceEquals(x.Envelope, evt)))
                {
                    buffer.Add(new PendingDomainEvent(schema, evt));
                }
            }
        }
    }
}
