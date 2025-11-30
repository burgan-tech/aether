using System.Collections.Generic;
using BBT.Aether.Events;

namespace BBT.Aether.Uow;

/// <summary>
/// Unit of Work implementation of IDomainEventSink.
/// Routes domain events from DbContext to the current active Unit of Work's transaction queue.
/// </summary>
public sealed class UnitOfWorkDomainEventSink(IUnitOfWorkManager unitOfWorkManager) : IDomainEventSink
{
    /// <inheritdoc />
    public void EnqueueDomainEvents(IEnumerable<DomainEventEnvelope> events)
    {
        var currentUow = unitOfWorkManager.Current;
        if (currentUow == null)
        {
            // No active UoW - this can happen when SaveChanges is called outside a UoW scope
            // We silently ignore this case to support flexibility in usage patterns
            return;
        }

        // If current UoW is a scope, we need to get the actual root UoW
        // UnitOfWorkScope is a wrapper that delegates to CompositeUnitOfWork
        var actualUow = currentUow;
        if (currentUow is UnitOfWorkScope scope)
        {
            actualUow = scope.Root;
        }

        // Get the actual UoW as IUnitOfWorkEventEnqueuer to access event enqueueing
        if (actualUow is not IUnitOfWorkEventEnqueuer eventEnqueuer)
        {
            // Current UoW doesn't support event enqueueing
            // This shouldn't happen with CompositeUnitOfWork, but we handle it gracefully
            return;
        }

        foreach (var envelope in events)
        {
            eventEnqueuer.EnqueueEvent(envelope);
        }
    }
}

/// <summary>
/// Internal interface for Unit of Work implementations that support event enqueueing.
/// Allows the sink to push events into the UoW without exposing this capability publicly.
/// </summary>
internal interface IUnitOfWorkEventEnqueuer
{
    /// <summary>
    /// Enqueues a domain event for later dispatching.
    /// </summary>
    /// <param name="eventEnvelope">The event envelope to enqueue</param>
    void EnqueueEvent(DomainEventEnvelope eventEnvelope);
}

