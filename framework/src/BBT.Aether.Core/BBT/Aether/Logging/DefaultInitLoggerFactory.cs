using System;
using System.Collections.Generic;

namespace BBT.Aether.Logging;

public sealed class DefaultInitLoggerFactory : IInitLoggerFactory
{
    private readonly Dictionary<Type, object> _cache = new Dictionary<Type, object>();

    public IInitLogger<T> Create<T>()
    {
        return (IInitLogger<T>)_cache.GetOrAdd(typeof(T), () => new DefaultInitLogger<T>()); ;
    }
}