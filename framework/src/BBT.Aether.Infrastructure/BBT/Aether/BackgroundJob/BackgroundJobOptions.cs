using System;
using System.Collections.Generic;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Configuration options for the background job system.
/// </summary>
public class BackgroundJobOptions
{
    /// <summary>
    /// Gets the list of registered job handler types.
    /// </summary>
    public List<JobHandlerRegistration> Handlers { get; } = new();

    /// <summary>
    /// Gets the dictionary of job invokers (created at registration time).
    /// Key: HandlerName, Value: Type-safe invoker (generic closed at startup).
    /// </summary>
    public Dictionary<string, IBackgroundJobInvoker> Invokers { get; } = new();

    /// <summary>
    /// Registers a job handler type.
    /// </summary>
    /// <typeparam name="THandler">The handler type that implements IBackgroundJobHandler&lt;TArgs&gt;.</typeparam>
    /// <param name="handlerName">The optional handler name (defaults to handler type name).</param>
    public void AddHandler<THandler>(string? handlerName = null)
        where THandler : class
    {
        var handlerType = typeof(THandler);
        var name = handlerName ?? handlerType.Name;

        Handlers.Add(new JobHandlerRegistration(name, handlerType));
    }

    /// <summary>The database schema whose background jobs this arming processor handles. The processor
    /// opens a UnitOfWork bound to this schema each run. If null/empty it logs a warning and does nothing.
    /// For multi-schema deployments run one processor instance per schema.</summary>
    public string? Schema { get; set; } = "sys_queues";

    /// <summary>Default maximum framework retry attempts for one-shot jobs (used by enqueue/dispatcher).</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Base delay for exponential retry backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval between arming-poller runs.</summary>
    public TimeSpan ArmingInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Max jobs armed per poller run.</summary>
    public int ArmingBatchSize { get; set; } = 100;

    /// <summary>How long a job may stay in Running before the reaper treats it as a crashed/timed-out
    /// execution and resets it for retry. Set this comfortably above your longest expected handler duration.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an arming claim is held before the reaper treats the claiming pod as crashed
    /// and clears its arming fields (ArmingToken/ArmingUntil) without changing Status.
    /// Must be comfortably longer than one arming pass.</summary>
    public TimeSpan ArmingLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    // ── Partitioning ──────────────────────────────────────────────────────────

    /// <summary>
    /// When true, the worker must acquire a slot from the <c>worker_slots</c> table before processing
    /// any jobs. Only <see cref="ActiveSlots"/> pods across the cluster may be active executors at once.
    /// When false (default) all pods are active executors — the previous behaviour.
    /// </summary>
    public bool PartitioningEnabled { get; set; } = false;

    /// <summary>
    /// Total number of pre-seeded rows in the <c>worker_slots</c> table.
    /// Only this many pods may be active background job executors simultaneously.
    /// Ignored when <see cref="PartitioningEnabled"/> is false.
    /// </summary>
    public int ActiveSlots { get; set; } = 2;

    /// <summary>How long a worker slot lease is held before it expires.</summary>
    public TimeSpan SlotLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the active executor renews its slot lease (heartbeat).
    /// Must be less than <see cref="SlotLeaseDuration"/>.
    /// </summary>
    public TimeSpan SlotHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Minimum jitter delay (seconds) when no owned partitions/slot could be acquired.</summary>
    public int EmptyPollMinDelaySeconds { get; set; } = 20;

    /// <summary>Maximum jitter delay (seconds) when no owned partitions/slot could be acquired.</summary>
    public int EmptyPollMaxDelaySeconds { get; set; } = 60;
}

/// <summary>
/// Represents a job handler registration.
/// </summary>
public class JobHandlerRegistration
{
    /// <summary>
    /// Initializes a new instance of the JobHandlerRegistration class.
    /// </summary>
    /// <param name="handlerName">The name of the handler (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="handlerType">The handler type.</param>
    public JobHandlerRegistration(string handlerName, Type handlerType)
    {
        HandlerName = handlerName ?? throw new ArgumentNullException(nameof(handlerName));
        HandlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
    }

    /// <summary>
    /// Gets the handler name.
    /// </summary>
    public string HandlerName { get; }

    /// <summary>
    /// Gets the handler type.
    /// </summary>
    public Type HandlerType { get; }
}

