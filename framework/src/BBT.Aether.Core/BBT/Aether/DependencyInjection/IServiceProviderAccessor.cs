using System;

namespace BBT.Aether.DependencyInjection;

public interface IServiceProviderAccessor
{
    IServiceProvider ServiceProvider { get; }
}