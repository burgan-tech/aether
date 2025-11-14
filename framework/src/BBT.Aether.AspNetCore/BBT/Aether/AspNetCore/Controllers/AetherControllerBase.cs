using System;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Mapper;
using BBT.Aether.Results;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Aether.AspNetCore.Controllers;

/// <summary>
/// Base controller that provides access to common services through ILazyServiceProvider.
/// Supports both constructor injection and AmbientServiceProvider fallback for flexibility.
/// </summary>
public abstract class AetherControllerBase : ControllerBase, IServiceProviderAccessor
{
    private IServiceProvider? _serviceProvider;
    private ILazyServiceProvider? _lazyServiceProvider;

    /// <summary>
    /// Gets the service provider. Uses HttpContext.RequestServices if available,
    /// falls back to AmbientServiceProvider if not.
    /// </summary>
    public IServiceProvider ServiceProvider
    {
        get
        {
            if (_serviceProvider != null)
            {
                return _serviceProvider;
            }

            // Try HttpContext.RequestServices first (most common in controllers)
            if (HttpContext?.RequestServices != null)
            {
                return HttpContext.RequestServices;
            }

            // Fallback to AmbientServiceProvider
            return AmbientServiceProvider.Current
                   ?? AmbientServiceProvider.Root
                   ?? throw new InvalidOperationException(
                       "No service provider available. Ensure the controller is invoked within an HTTP request context or AmbientServiceProvider is configured.");
        }
        set => _serviceProvider = value;
    }

    /// <summary>
    /// Gets the lazy service provider for property-level service resolution.
    /// </summary>
    protected ILazyServiceProvider LazyServiceProvider
    {
        get
        {
            if (_lazyServiceProvider != null)
            {
                return _lazyServiceProvider;
            }

            _lazyServiceProvider = ServiceProvider.GetRequiredService<ILazyServiceProvider>();
            return _lazyServiceProvider;
        }
    }

    /// <summary>
    /// Gets the current user.
    /// </summary>
    protected ICurrentUser CurrentUser => LazyServiceProvider.LazyGetRequiredService<ICurrentUser>();

    /// <summary>
    /// Gets the GUID generator.
    /// </summary>
    protected IGuidGenerator GuidGenerator => LazyServiceProvider.LazyGetRequiredService<IGuidGenerator>();

    /// <summary>
    /// Gets the object mapper.
    /// </summary>
    protected IObjectMapper ObjectMapper => LazyServiceProvider.LazyGetRequiredService<IObjectMapper>();

    /// <summary>
    /// Gets the logger factory.
    /// </summary>
    protected ILoggerFactory LoggerFactory => LazyServiceProvider.LazyGetRequiredService<ILoggerFactory>();

    /// <summary>
    /// Gets a logger for this controller type.
    /// </summary>
    protected ILogger Logger => LoggerFactory?.CreateLogger(GetType().FullName!) ?? NullLogger.Instance;

    /// <summary>
    /// Converts a Result to an ActionResult.
    /// Success returns 200 OK, failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <param name="result">The result to convert</param>
    /// <returns>ActionResult representing the operation result</returns>
    protected IActionResult FromResult(Result result)
    {
        return result.ToActionResult(HttpContext);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to an ActionResult.
    /// Success returns the value with 200 OK (or 204 NoContent if value is null).
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="statusCodeSelector">Optional function to customize the success status code based on the value</param>
    /// <returns>ActionResult representing the operation result</returns>
    protected IActionResult FromResult<T>(Result<T> result, Func<T, int>? statusCodeSelector = null)
    {
        return result.ToActionResult(HttpContext, statusCodeSelector);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to a CreatedResult (201 Created).
    /// Success returns the value with 201 Created status.
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="location">Optional location URI for the created resource</param>
    /// <returns>ActionResult representing the operation result</returns>
    protected IActionResult FromResultCreated<T>(Result<T> result, string? location = null)
    {
        return result.ToCreatedResult(HttpContext, location);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to an AcceptedResult (202 Accepted).
    /// Success returns the value with 202 Accepted status.
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="location">Optional location URI to check the status of the request</param>
    /// <returns>ActionResult representing the operation result</returns>
    protected IActionResult FromResultAccepted<T>(Result<T> result, string? location = null)
    {
        return result.ToAcceptedResult(HttpContext, location);
    }
}

