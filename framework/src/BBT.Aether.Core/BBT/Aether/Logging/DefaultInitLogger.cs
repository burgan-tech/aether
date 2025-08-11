using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Logging;

public sealed class DefaultInitLogger<T> : IInitLogger<T>
{
    public List<AetherInitLogEntry> Entries { get; } = new();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new AetherInitLogEntry
        {
            LogLevel = logLevel,
            EventId = eventId,
            State = state!,
            Exception = exception,
            Formatter = (s, e) => formatter((TState)s, e),
        });
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullDisposable.Instance;
    }
}