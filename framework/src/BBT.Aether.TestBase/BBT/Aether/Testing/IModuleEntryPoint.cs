using System;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Testing;

public interface IModuleEntryPoint
{
    /// <summary>
    /// Adds the relevant modules of the application to the service collection.
    /// </summary>
    /// <param name="services"></param>
    void Load(IServiceCollection services);

    /// <summary>
    /// After the service provider is created, it executes the initialize operations.
    /// For example, database migration and seed operations can be performed here.
    /// </summary>
    /// <param name="serviceProvider"></param>
    void OnInitialize(IServiceProvider serviceProvider);
}