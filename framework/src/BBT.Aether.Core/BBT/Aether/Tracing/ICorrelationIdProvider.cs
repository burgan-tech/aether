using System;

namespace BBT.Aether.Tracing;

/// <summary>
/// Provides access to the current correlation ID.
/// </summary>
public interface ICorrelationIdProvider
{
    /// <summary>
    /// Gets the current correlation ID.
    /// </summary>
    /// <returns>The current correlation ID, or <see langword="null"/> if no correlation ID is set.</returns>
    string? Get();

    /// <summary>
    /// Changes the current correlation ID for the duration of the returned <see cref="IDisposable"/>.
    /// </summary>
    /// <param name="correlationId">The new correlation ID.  Use <see langword="null"/> to clear the current correlation ID.</param>
    /// <returns>An <see cref="IDisposable"/> that restores the previous correlation ID when disposed.</returns>
    IDisposable Change(string? correlationId);
}