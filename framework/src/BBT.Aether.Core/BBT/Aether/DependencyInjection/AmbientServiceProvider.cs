using System;
using System.Threading;

namespace BBT.Aether.DependencyInjection;

/// <summary>
/// Provides ambient (AsyncLocal-based) access to IServiceProvider for use in aspects and other cross-cutting concerns.
/// The Current property propagates across async call chains within the same execution context.
/// </summary>
public static class AmbientServiceProvider
{
    private static readonly AsyncLocal<IServiceProvider?> _current = new();

    /// <summary>
    /// Gets or sets the current service provider for this async context.
    /// This value is automatically propagated across async/await boundaries.
    /// Set by middleware for ASP.NET Core requests or manually for console/worker apps.
    /// </summary>
    public static IServiceProvider? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// Gets or sets the root service provider used as fallback when Current is null.
    /// Typically set once at application startup from IApplicationBuilder.ApplicationServices.
    /// </summary>
    public static IServiceProvider? Root { get; set; }
}

