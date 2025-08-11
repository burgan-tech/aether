using System;

namespace BBT.Aether.Guids;

public sealed class SequentialGuidGenerator : IGuidGenerator
{
    public static SequentialGuidGenerator Instance { get; } = new();
    public Guid Create()
    {
        return Guid.CreateVersion7(DateTimeOffset.UtcNow);
    }
}