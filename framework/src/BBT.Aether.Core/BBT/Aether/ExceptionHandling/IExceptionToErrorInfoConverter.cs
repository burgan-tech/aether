using System;
using BBT.Aether.Http;
using BBT.Aether.Results;

namespace BBT.Aether.ExceptionHandling;

/// <summary>
/// This interface can be implemented to convert an <see cref="Exception"/> object to an <see cref="ServiceErrorInfo"/> object.
/// Implements Chain Of Responsibility pattern.
/// </summary>
public interface IExceptionToErrorInfoConverter
{
    /// <summary>
    /// Converter method.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="options">Additional options.</param>
    /// <returns>Error info or null</returns>
    ServiceErrorInfo Convert(Exception exception, Action<AetherExceptionHandlingOptions>? options = null);
    
    /// <summary>
    /// Converter method to Error object.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="options">Additional options.</param>
    /// <returns>Error object</returns>
    Error ConvertToError(Exception exception, Action<AetherExceptionHandlingOptions>? options = null);
}
