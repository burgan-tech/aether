namespace BBT.Aether.Events;

/// <summary>
/// Defines the strategy for dispatching domain events.
/// </summary>
public enum DomainEventDispatchStrategy
{
    /// <summary>
    /// Always write events to outbox within the transaction.
    /// Events are dispatched by the OutboxProcessor.
    /// This provides maximum reliability as events are persisted atomically with business data.
    /// </summary>
    AlwaysUseOutbox,
    
    /// <summary>
    /// Publish events directly after commit.
    /// On publish failure, write to outbox in a new scope.
    /// This provides lower latency but requires the broker to be available.
    /// </summary>
    PublishWithFallback
}

/// <summary>
/// Configuration options for domain event dispatching.
/// </summary>
public class AetherDomainEventOptions
{
    /// <summary>
    /// Gets or sets the dispatch strategy for domain events.
    /// Default is AlwaysUseOutbox for maximum reliability.
    /// </summary>
    public DomainEventDispatchStrategy DispatchStrategy { get; set; } = DomainEventDispatchStrategy.AlwaysUseOutbox;

    /// <summary>
    /// Gets or sets whether a non-transactional unit of work (one with no shared database
    /// transaction — e.g. a flow that persists via per-step <c>autoSave</c> rather than a single
    /// enclosing transaction) also flushes its buffered domain events when it commits.
    /// <para>
    /// Default is <see langword="false"/> — the historical behavior: without a transaction the
    /// unit of work cannot co-commit events atomically with the business data, so buffered events
    /// are NOT dispatched and are effectively dropped. Callers that need events in such flows have
    /// to publish them explicitly.
    /// </para>
    /// <para>
    /// When set to <see langword="true"/>, on commit the unit of work dispatches its buffered
    /// events using the configured <see cref="DispatchStrategy"/> even though no transaction was
    /// opened. The business data has already been durably written by the per-step saves, so this
    /// restores at-least-once delivery for these flows; it is NOT atomic with the business writes
    /// (the event write is a separate durable write), so a crash between the two relies on the
    /// consumer's idempotent retry/recovery. This is config-gated precisely so it can be disabled
    /// at deploy time — without a code change — if it causes issues in production.
    /// </para>
    /// </summary>
    public bool DispatchNonTransactionalEventsToOutbox { get; set; }
}

