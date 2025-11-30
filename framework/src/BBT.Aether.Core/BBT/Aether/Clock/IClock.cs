using System;

namespace BBT.Aether.Clock;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTimeOffset UtcNowOffset { get; }
    
    DateTime NormalizeToUtc(DateTime dt);
    DateTimeOffset NormalizeToUtc(DateTimeOffset dto);
}