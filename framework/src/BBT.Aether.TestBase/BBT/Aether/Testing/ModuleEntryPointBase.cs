using System;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Testing;

public abstract class ModuleEntryPointBase : IModuleEntryPoint
{
    public abstract void Load(IServiceCollection services);

    /// <summary>
    /// By default, it does nothing.
    /// If there are special operations that require initialization in the entry point, it can be overridden.
    /// </summary>
    /// <param name="serviceProvider">Created service provider</param>
    public virtual void OnInitialize(IServiceProvider serviceProvider)
    {
        // No-op
    }
}