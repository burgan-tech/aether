using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Logging;

public interface IInitLogger<out T> : ILogger<T>
{
    public List<AetherInitLogEntry> Entries { get; }
}