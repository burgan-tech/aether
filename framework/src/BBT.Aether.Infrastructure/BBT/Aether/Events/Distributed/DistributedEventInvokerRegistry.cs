using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace BBT.Aether.Events;

/// <summary>
/// Registry of precompiled event invokers.
/// Built at startup from discovered event handlers.
/// </summary>
public sealed class DistributedEventInvokerRegistry : IDistributedEventInvokerRegistry
{
    private readonly Dictionary<(string name, int version), IDistributedEventInvoker> _map = new();
    private readonly List<IDistributedEventInvoker> _allInvokers;

    /// <summary>
    /// Creates a new registry from a collection of invokers.
    /// </summary>
    /// <param name="invokers">The invokers to register</param>
    public DistributedEventInvokerRegistry(IEnumerable<IDistributedEventInvoker> invokers)
    {
        _allInvokers = invokers.ToList();
        
        foreach (var invoker in _allInvokers)
        {
            var key = (invoker.Name, invoker.Version);
            _map[key] = invoker;
        }
    }

    /// <inheritdoc />
    public bool TryGet(string name, int version, [NotNullWhen(true)] out IDistributedEventInvoker? invoker)
    {
        return _map.TryGetValue((name, version), out invoker);
    }

    /// <inheritdoc />
    public IEnumerable<IDistributedEventInvoker> All => _allInvokers;
}

