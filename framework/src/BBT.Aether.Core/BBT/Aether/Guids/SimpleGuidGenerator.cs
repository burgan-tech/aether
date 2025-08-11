using System;

namespace BBT.Aether.Guids;

public sealed class SimpleGuidGenerator: IGuidGenerator
{
    public static SimpleGuidGenerator Instance { get; } = new();

    public Guid Create()
    {   
        return Guid.NewGuid();
    }
}
