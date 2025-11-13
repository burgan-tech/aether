using System;
using System.Collections.Generic;

namespace BBT.Aether;

public sealed class AetherSubscription<T>(IList<T> list, T handler) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        list.Remove(handler);
    }
}

public sealed class NoOpDisposable : IDisposable
{
    public readonly static NoOpDisposable Instance = new();

    private NoOpDisposable() { }

    public void Dispose()
    {
        // No-op
    }
}
