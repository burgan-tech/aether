using System;

namespace BBT.Aether;

/// <summary>
/// Base exception type for those are thrown by Aether system for Aether specific exceptions.
/// </summary>
public class AetherException : Exception
{
    protected AetherException()
    {

    }

    public AetherException(string? message)
        : base(message)
    {

    }

    public AetherException(string? message, Exception? innerException)
        : base(message, innerException)
    {

    }
}