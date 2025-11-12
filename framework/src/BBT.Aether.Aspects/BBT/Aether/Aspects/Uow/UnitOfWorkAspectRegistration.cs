using System;
using System.Collections.Generic;
using PostSharp.Aspects;
using PostSharp.Aspects.Dependencies;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Registration attribute for automatic UnitOfWork aspect application.
/// Place this attribute at assembly level to enable automatic UnitOfWork for IApplicationService implementations.
/// This attribute directly registers the UnitOfWorkAspectProvider with PostSharp.
/// </summary>
/// <example>
/// <code>
/// // In AssemblyInfo.cs or any .cs file in your project:
/// [assembly: BBT.Aether.Aspects.AutoUnitOfWork]
/// </code>
/// </example>
[PSerializable]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
[AspectTypeDependency(AspectDependencyAction.Order, AspectDependencyPosition.Before, typeof(UnitOfWorkAttribute))]
public sealed class AutoUnitOfWorkAttribute : Attribute, IAspectProvider
{
    [PNonSerialized]
    private UnitOfWorkAspectProvider? _provider;

    /// <summary>
    /// Gets or sets whether automatically applied UnitOfWork should be transactional.
    /// Default is false.
    /// </summary>
    public bool IsTransactional { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to apply UnitOfWork to read-only methods (Get*, Find*, etc.).
    /// Default is true (applies to all public methods).
    /// </summary>
    public bool IncludeReadOnlyMethods { get; set; } = true;

    /// <summary>
    /// Provides aspects to target elements through the UnitOfWorkAspectProvider.
    /// </summary>
    public IEnumerable<AspectInstance> ProvideAspects(object targetElement)
    {
        // Lazy initialize provider
        if (_provider == null)
        {
            _provider = new UnitOfWorkAspectProvider();
            
            // Apply configuration from attribute properties
            if (IsTransactional)
            {
                var config = new UnitOfWorkConfiguration { IsTransactional = IsTransactional };
                UnitOfWorkAspectProvider.Configure(config);
            }
        }

        return _provider.ProvideAspects(targetElement);
    }
}

/// <summary>
/// Extension methods for configuring automatic UnitOfWork behavior.
/// </summary>
public static class UnitOfWorkConfigurationExtensions
{
    /// <summary>
    /// Configures the automatic UnitOfWork behavior.
    /// Call this in your application startup (before PostSharp compilation).
    /// </summary>
    /// <param name="configure">Configuration action</param>
    public static void ConfigureAutoUnitOfWork(Action<UnitOfWorkConfiguration> configure)
    {
        if (configure == null)
            throw new ArgumentNullException(nameof(configure));

        var config = new UnitOfWorkConfiguration();
        configure(config);
        UnitOfWorkAspectProvider.Configure(config);
    }

    /// <summary>
    /// Registers a custom marker interface for automatic UnitOfWork application.
    /// Any type implementing this interface will have UnitOfWork automatically applied to its methods.
    /// </summary>
    /// <typeparam name="TMarkerInterface">The marker interface type</typeparam>
    public static void RegisterUnitOfWorkMarkerInterface<TMarkerInterface>()
        where TMarkerInterface : class
    {
        var type = typeof(TMarkerInterface);
        UnitOfWorkAspectProvider.AddMarkerInterface(type);
    }

    /// <summary>
    /// Registers a custom marker interface for automatic UnitOfWork application.
    /// Any type implementing this interface will have UnitOfWork automatically applied to its methods.
    /// </summary>
    /// <param name="interfaceType">The marker interface type</param>
    public static void RegisterUnitOfWorkMarkerInterface(Type interfaceType)
    {
        if (interfaceType == null)
            throw new ArgumentNullException(nameof(interfaceType));

        UnitOfWorkAspectProvider.AddMarkerInterface(interfaceType);
    }
}

