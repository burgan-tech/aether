using System;

namespace BBT.Aether.Guids;

/// <summary>
/// Defines a service for generating Guids.
/// </summary>
public interface IGuidGenerator
{
    /// <summary>
    /// Creates a new Guid.
    /// </summary>
    /// <returns>A new Guid.</returns>
    Guid Create();
}