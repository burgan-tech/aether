using System;
using System.Threading.Tasks;
using BBT.Aether.Aspects;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class UnitOfWorkAspectTests
{
    [Fact]
    public async Task Transactional_required_attribute_rejects_nontransactional_ambient_root()
    {
        await using var serviceProvider = BuildProvider();
        await using var serviceScope = serviceProvider.CreateAsyncScope();
        var previous = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = serviceScope.ServiceProvider;
        try
        {
            var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var owner = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });

            var exception = await Should.ThrowAsync<InvalidOperationException>(
                AspectProbe.TransactionalRequiredAsync);

            exception.Message.ShouldContain("RequiresNew");
            owner.IsAborted.ShouldBeFalse();
        }
        finally
        {
            AmbientServiceProvider.Current = previous;
        }
    }

    [Fact]
    public async Task Required_attribute_exception_aborts_ambient_root_even_when_caller_catches_it()
    {
        await using var serviceProvider = BuildProvider();
        await using var serviceScope = serviceProvider.CreateAsyncScope();
        var previous = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = serviceScope.ServiceProvider;
        try
        {
            var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var owner = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });

            await Should.ThrowAsync<ProbeException>(AspectProbe.ThrowAsync);

            owner.IsAborted.ShouldBeTrue();
            await Should.ThrowAsync<InvalidOperationException>(() => owner.CommitAsync());
        }
        finally
        {
            AmbientServiceProvider.Current = previous;
        }
    }

    [Fact]
    public async Task RequiresNew_attribute_uses_independent_root_and_restores_ambient_owner()
    {
        await using var serviceProvider = BuildProvider();
        await using var serviceScope = serviceProvider.CreateAsyncScope();
        var previous = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = serviceScope.ServiceProvider;
        try
        {
            var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var owner = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            });
            IUnitOfWork? observed = null;

            await AspectProbe.ObserveRequiresNewAsync(current => observed = current);

            observed.ShouldNotBeNull();
            observed.Id.ShouldNotBe(owner.Id);
            observed.IsCompleted.ShouldBeTrue();
            manager.Current.ShouldBeSameAs(owner);
        }
        finally
        {
            AmbientServiceProvider.Current = previous;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();
        services.AddScoped<IUnitOfWorkManager, UnitOfWorkManager>();
        return services.BuildServiceProvider();
    }

    private static class AspectProbe
    {
        [UnitOfWork(IsTransactional = true)]
        public static Task TransactionalRequiredAsync() => Task.CompletedTask;

        [UnitOfWork]
        public static Task ThrowAsync() => Task.FromException(new ProbeException());

        [UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]
        public static Task ObserveRequiresNewAsync(Action<IUnitOfWork?> observer)
        {
            var manager = AmbientServiceProvider.Current!
                .GetRequiredService<IUnitOfWorkManager>();
            observer(manager.Current);
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeException : Exception;
}
