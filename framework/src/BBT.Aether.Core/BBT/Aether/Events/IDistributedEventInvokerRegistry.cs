using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BBT.Aether.Events;

/// <summary>
/// Registry of event invokers built at startup time.
/// Maps (event name, version) to precompiled handler invokers.
/// </summary>
public interface IDistributedEventInvokerRegistry
{
    /// <summary>
    /// Attempts to get an invoker by event name and version.
    /// </summary>
    /// <param name="name">Event name</param>
    /// <param name="version">Event version</param>
    /// <param name="invoker">The invoker if found</param>
    /// <returns>True if invoker was found, false otherwise</returns>
    bool TryGet(string name, int version, [NotNullWhen(true)] out IDistributedEventInvoker? invoker);
    
    /// <summary>
    /// Gets all registered invokers for building Dapr subscription list.
    /// </summary>
    IEnumerable<IDistributedEventInvoker> All { get; }
}

