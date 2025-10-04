using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events.Distributed;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Default implementation of <see cref="IEventContext"/> that provides unified event handling.
/// Manages pre-commit, post-commit, and distributed events through their respective handlers.
/// </summary>
public sealed class EventContext : IEventContext
{
    private readonly IDomainEventDispatcher? _domainEventDispatcher;
    private readonly IDistributedDomainEventPublisher? _distributedEventPublisher;
    private readonly ILogger<EventContext> _logger;
    private readonly List<IPostCommitEvent> _storedPostCommitEvents = new();
    private readonly List<IDistributedDomainEvent> _storedDistributedEvents = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EventContext"/> class.
    /// </summary>
    /// <param name="domainEventDispatcher">The domain event dispatcher for local events.</param>
    /// <param name="distributedEventPublisher">The distributed event publisher for external events.</param>
    /// <param name="logger">The logger instance.</param>
    public EventContext(
        IDomainEventDispatcher? domainEventDispatcher,
        IDistributedDomainEventPublisher? distributedEventPublisher,
        ILogger<EventContext> logger)
    {
        _domainEventDispatcher = domainEventDispatcher;
        _distributedEventPublisher = distributedEventPublisher;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool HasStoredEvents => _storedPostCommitEvents.Count > 0 || _storedDistributedEvents.Count > 0;

    /// <inheritdoc />
    public async Task DispatchPreCommitEventsAsync(IEnumerable<IPreCommitEvent> events, CancellationToken cancellationToken = default)
    {
        if (events?.Any() != true)
        {
            _logger.LogDebug("No pre-commit events to dispatch");
            return;
        }

        if (_domainEventDispatcher == null)
        {
            _logger.LogWarning("No domain event dispatcher registered. Pre-commit events will be ignored");
            return;
        }

        var eventList = events.ToList();
        _logger.LogDebug("Dispatching {Count} pre-commit events", eventList.Count);

        try
        {
            await _domainEventDispatcher.DispatchAsync(eventList, cancellationToken);
            _logger.LogDebug("Successfully dispatched {Count} pre-commit events", eventList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Count} pre-commit events", eventList.Count);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DispatchPostCommitEventsAsync(IEnumerable<IPostCommitEvent> events, CancellationToken cancellationToken = default)
    {
        if (events?.Any() != true)
        {
            _logger.LogDebug("No post-commit events to dispatch");
            return;
        }

        if (_domainEventDispatcher == null)
        {
            _logger.LogWarning("No domain event dispatcher registered. Post-commit events will be ignored");
            return;
        }

        var eventList = events.ToList();
        _logger.LogDebug("Dispatching {Count} post-commit events", eventList.Count);

        try
        {
            await _domainEventDispatcher.DispatchAsync(eventList, cancellationToken);
            _logger.LogDebug("Successfully dispatched {Count} post-commit events", eventList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Count} post-commit events", eventList.Count);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PublishDistributedEventsAsync(IEnumerable<IDistributedDomainEvent> events, CancellationToken cancellationToken = default)
    {
        if (events?.Any() != true)
        {
            _logger.LogDebug("No distributed events to publish");
            return;
        }

        if (_distributedEventPublisher == null)
        {
            _logger.LogWarning("No distributed event publisher registered. Distributed events will be ignored");
            return;
        }

        var eventList = events.ToList();
        _logger.LogDebug("Publishing {Count} distributed events", eventList.Count);

        try
        {
            await _distributedEventPublisher.PublishAsync(eventList, cancellationToken);
            _logger.LogDebug("Successfully published {Count} distributed events", eventList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {Count} distributed events", eventList.Count);
            throw;
        }
    }

    /// <inheritdoc />
    public void StorePostCommitEvents(IEnumerable<IPostCommitEvent> events)
    {
        if (events?.Any() == true)
        {
            var eventList = events.ToList();
            _storedPostCommitEvents.AddRange(eventList);
            _logger.LogDebug("Stored {Count} post-commit events for later dispatch", eventList.Count);
        }
    }

    /// <inheritdoc />
    public void StoreDistributedEvents(IEnumerable<IDistributedDomainEvent> events)
    {
        if (events?.Any() == true)
        {
            var eventList = events.ToList();
            _storedDistributedEvents.AddRange(eventList);
            _logger.LogDebug("Stored {Count} distributed events for later dispatch", eventList.Count);
        }
    }

    /// <inheritdoc />
    public async Task DispatchStoredEventsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Dispatch stored post-commit events
            if (_storedPostCommitEvents.Count > 0)
            {
                _logger.LogDebug("Dispatching {Count} stored post-commit events", _storedPostCommitEvents.Count);
                await DispatchPostCommitEventsAsync(_storedPostCommitEvents, cancellationToken);
            }

            // Publish stored distributed events
            if (_storedDistributedEvents.Count > 0)
            {
                _logger.LogDebug("Publishing {Count} stored distributed events", _storedDistributedEvents.Count);
                await PublishDistributedEventsAsync(_storedDistributedEvents, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch stored events. Database changes are already committed.");
            // Note: We don't rethrow here because the database transaction was already committed successfully
            // Event dispatch failures should be handled separately (e.g., retry mechanisms, dead letter queues)
        }
    }

    /// <inheritdoc />
    public void ClearStoredEvents()
    {
        if (_storedPostCommitEvents.Count > 0 || _storedDistributedEvents.Count > 0)
        {
            _logger.LogDebug("Clearing {PostCommitCount} stored post-commit events and {DistributedCount} stored distributed events", 
                _storedPostCommitEvents.Count, _storedDistributedEvents.Count);
            
            _storedPostCommitEvents.Clear();
            _storedDistributedEvents.Clear();
        }
    }
}
