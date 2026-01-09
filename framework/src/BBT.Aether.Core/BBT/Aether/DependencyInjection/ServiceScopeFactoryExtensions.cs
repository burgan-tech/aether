using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.DependencyInjection;

/// <summary>
/// Extension methods for IServiceScopeFactory.
/// </summary>
public static class ServiceScopeFactoryExtensions
{
    /// <summary>
    /// Executes the given action within a new dependency injection scope and a new unit of work.
    /// Manages ambient service provider propagation.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The action to execute, receiving the scoped service provider.</param>
    /// <param name="options">Unit of work options. Defaults to a new UoW with default settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task ExecuteInNewUnitOfWorkScopeAsync(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, Task> action,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Propagate ambient service provider for the new scope
        var previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = sp;

        try
        {
            var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();

            // Begin UnitOfWork
            await using var uow = await uowManager.BeginAsync(options, cancellationToken);
            
            // Execute action
            await action(sp);
            
            // Commit UnitOfWork
            await uow.CommitAsync(cancellationToken);
        }
        finally
        {
            // Restore previous ambient context
            AmbientServiceProvider.Current = previousAmbient;
        }
    }

    /// <summary>
    /// Executes the given function within a new dependency injection scope and a new unit of work, returning a result.
    /// Manages ambient service provider propagation.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="func">The function to execute, receiving the scoped service provider.</param>
    /// <param name="options">Unit of work options. Defaults to a new UoW with default settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the function execution.</returns>
    public static async Task<T> ExecuteInNewUnitOfWorkScopeAsync<T>(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, Task<T>> func,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Propagate ambient service provider for the new scope
        var previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = sp;

        try
        {
            var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();

            // Begin UnitOfWork
            await using var uow = await uowManager.BeginAsync(options, cancellationToken);
            
            // Execute function
            var result = await func(sp);
            
            // Commit UnitOfWork
            await uow.CommitAsync(cancellationToken);

            return result;
        }
        finally
        {
            // Restore previous ambient context
            AmbientServiceProvider.Current = previousAmbient;
        }
    }

    /// <summary>
    /// Executes the given action within a new dependency injection scope without starting a unit of work.
    /// Manages ambient service provider propagation.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The action to execute, receiving the scoped service provider.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task ExecuteInNewScopeAsync(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Propagate ambient service provider for the new scope
        var previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = sp;

        try
        {
            await action(sp);
        }
        finally
        {
            // Restore previous ambient context
            AmbientServiceProvider.Current = previousAmbient;
        }
    }

    /// <summary>
    /// Executes the given function within a new dependency injection scope without starting a unit of work, returning a result.
    /// Manages ambient service provider propagation.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="func">The function to execute, receiving the scoped service provider.</param>
    /// <returns>The result of the function execution.</returns>
    public static async Task<T> ExecuteInNewScopeAsync<T>(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, Task<T>> func)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Propagate ambient service provider for the new scope
        var previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = sp;

        try
        {
            return await func(sp);
        }
        finally
        {
            // Restore previous ambient context
            AmbientServiceProvider.Current = previousAmbient;
        }
    }
}
}
