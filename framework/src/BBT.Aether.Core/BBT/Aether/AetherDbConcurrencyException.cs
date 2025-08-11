using System;

namespace BBT.Aether;

public class AetherDbConcurrencyException : AetherException
{
    /// <summary>
    /// Creates a new <see cref="AetherDbConcurrencyException"/> object.
    /// </summary>
    public AetherDbConcurrencyException()
    {
    }

    /// <summary>
    /// Creates a new <see cref="AetherDbConcurrencyException"/> object.
    /// </summary>
    /// <param name="message">Exception message</param>
    public AetherDbConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="AetherDbConcurrencyException"/> object.
    /// </summary>
    /// <param name="message">Exception message</param>
    /// <param name="innerException">Inner exception</param>
    public AetherDbConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}