using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Testing;

public abstract class TestBase : TestBaseWithServiceProvider, IDisposable
{
    protected TestBase()
    {
        var services = CreateServiceCollection();
        services.AddAetherCore(_ => {});
        RegisterConfiguration(services);
        AddApplication(services);

        ServiceProvider = CreateServiceProvider(services);

        OnInitialization();
    }

    protected virtual IServiceCollection CreateServiceCollection()
    {
        return new ServiceCollection();
    }

    protected abstract void AddApplication(IServiceCollection services);

    protected virtual IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        return services.BuildServiceProvider();
    }

    protected virtual void OnInitialization()
    {
        // No-op
    }

    public virtual void Dispose()
    {
        // No-op
    }

    protected virtual void RegisterConfiguration(IServiceCollection services)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        services.AddSingleton(configuration);
    }
}

public abstract class TestBase<TEntry> : TestBase
    where TEntry : ModuleEntryPointBase, new()
{
    private readonly TEntry _entryPoint = new();

    protected override void AddApplication(IServiceCollection services)
    {
        _entryPoint.Load(services);
    }

    protected override void OnInitialization()
    {
        _entryPoint.OnInitialize(ServiceProvider);
    }
}