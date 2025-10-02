using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Default implementation of <see cref="IDomainEventDispatcher"/> that dispatches events to registered handlers.
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventDispatcher"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider to resolve handlers from.</param>
    public DomainEventDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        if (events == null)
            return;

        foreach (var @event in events)
        {
            await DispatchEventAsync(@event, cancellationToken);
        }
    }

    private async Task DispatchEventAsync(IDomainEvent @event, CancellationToken cancellationToken)
    {
        var eventType = @event.GetType();
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var enumerableHandlerType = typeof(IEnumerable<>).MakeGenericType(handlerType);
        
        var handlers = (IEnumerable<object>?)_serviceProvider.GetService(enumerableHandlerType) 
                       ?? Array.Empty<object>();

        // Sort handlers by order if they implement IOrderedDomainEventHandler
        var orderedHandlers = handlers
            .Select(handler => new
            {
                Handler = handler,
                Order = GetHandlerOrder(handler, eventType)
            })
            .OrderBy(x => x.Order)
            .Select(x => x.Handler);

        foreach (var handler in orderedHandlers)
        {
            var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;
            var task = (Task)handleMethod.Invoke(handler, new object[] { @event, cancellationToken })!;
            await task.ConfigureAwait(false);
        }
    }

    private static int GetHandlerOrder(object handler, Type eventType)
    {
        // Check if handler implements IOrderedDomainEventHandler<TEvent>
        var orderedHandlerType = typeof(IOrderedDomainEventHandler<>).MakeGenericType(eventType);
        
        if (orderedHandlerType.IsAssignableFrom(handler.GetType()))
        {
            var orderProperty = orderedHandlerType.GetProperty(nameof(IOrderedDomainEventHandler<IDomainEvent>.Order));
            if (orderProperty?.GetValue(handler) is int order)
            {
                return order;
            }
        }

        // Default order is 0 for handlers that don't implement IOrderedDomainEventHandler
        return 0;
    }
}
