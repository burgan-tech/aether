using System;

namespace BBT.Aether.Clock;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow; // Kind = Utc
    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow; // Offset = 0
    
    public DateTime NormalizeToUtc(DateTime dt)
        => dt.Kind switch
        {
            DateTimeKind.Utc         => dt,
            DateTimeKind.Local       => dt.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _                        => dt
        };

    public DateTimeOffset NormalizeToUtc(DateTimeOffset dto)
        => dto.ToUniversalTime();
}