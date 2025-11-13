using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using PostSharp.Aspects;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Base class for method interception aspects with extensibility points.
/// Provides virtual methods for pre-processing, post-processing, and exception handling.
/// SDK users can extend this class to create custom aspects with specialized behavior.
/// </summary>
[PSerializable]
public abstract class AetherMethodInterceptionAspect : MethodInterceptionAspect
{
    /// <summary>
    /// Called before the intercepted method executes (async version).
    /// Override to add custom pre-processing logic.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    protected virtual Task OnBeforeAsync(MethodInterceptionArgs args)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after the intercepted method completes successfully (async version).
    /// Override to add custom post-processing logic.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    protected virtual Task OnAfterAsync(MethodInterceptionArgs args)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the intercepted method throws an exception (async version).
    /// Override to add custom exception handling logic.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    /// <param name="ex">The exception that was thrown</param>
    protected virtual Task OnExceptionAsync(MethodInterceptionArgs args, Exception ex)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before the intercepted method executes (sync version).
    /// Override to add custom pre-processing logic for synchronous methods.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    protected virtual void OnBefore(MethodInterceptionArgs args)
    {
    }

    /// <summary>
    /// Called after the intercepted method completes successfully (sync version).
    /// Override to add custom post-processing logic for synchronous methods.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    protected virtual void OnAfter(MethodInterceptionArgs args)
    {
    }

    /// <summary>
    /// Called when the intercepted method throws an exception (sync version).
    /// Override to add custom exception handling logic for synchronous methods.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    /// <param name="ex">The exception that was thrown</param>
    protected virtual void OnException(MethodInterceptionArgs args, Exception ex)
    {
    }

    /// <summary>
    /// Extracts the CancellationToken parameter from the method arguments, if present.
    /// Returns CancellationToken.None if no CancellationToken parameter is found.
    /// </summary>
    /// <param name="args">Method interception arguments</param>
    /// <returns>The CancellationToken from method parameters or CancellationToken.None</returns>
    protected static CancellationToken ExtractCancellationToken(MethodInterceptionArgs args)
    {
        var parameters = args.Method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(CancellationToken) && 
                args.Arguments[i] is CancellationToken ct)
            {
                return ct;
            }
        }
        return CancellationToken.None;
    }

    /// <summary>
    /// Gets the current service provider from AmbientServiceProvider.
    /// Throws InvalidOperationException if no service provider is available.
    /// </summary>
    /// <returns>The current IServiceProvider</returns>
    /// <exception cref="InvalidOperationException">Thrown when AmbientServiceProvider is not set</exception>
    protected static IServiceProvider GetServiceProvider()
    {
        return AmbientServiceProvider.Current ?? AmbientServiceProvider.Root
            ?? throw new InvalidOperationException(
                "AmbientServiceProvider.Current not set. " +
                "Ensure UseAetherAmbientServiceProvider() is called in ASP.NET Core pipeline, " +
                "or manually set AmbientServiceProvider.Current in console/worker applications.");
    }
}

