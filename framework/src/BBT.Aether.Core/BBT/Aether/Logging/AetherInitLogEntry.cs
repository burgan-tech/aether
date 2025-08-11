using System;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Logging;

public class AetherInitLogEntry
{
    public LogLevel LogLevel { get; set; }

    public EventId EventId { get; set; }

    public object State { get; set; } = null!;

    public Exception? Exception { get; set; }

    public Func<object, Exception?, string> Formatter { get; set; } = null!;

    public string Message => Formatter(State, Exception);
}