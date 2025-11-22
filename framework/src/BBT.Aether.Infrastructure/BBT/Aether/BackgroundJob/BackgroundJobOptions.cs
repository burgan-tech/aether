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

