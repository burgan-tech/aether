using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Extension methods for IUnitOfWorkManager to simplify common usage patterns.
/// </summary>
public static class UnitOfWorkManagerExtensions
{
    /// <summary>
    /// Executes an action within a Unit of Work with automatic commit/rollback handling.
    /// Commits on success, rolls back on exception.
    /// </summary>
    /// <param name="uowManager">The unit of work manager</param>
    /// <param name="action">The action to execute</param>
    /// <param name="options">Optional UoW configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async static Task ExecuteInUowAsync(
        this IUnitOfWorkManager uowManager,
        Func<CancellationToken, Task> action,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Synchronous begin so the unit of work is ambient in this frame for the user action,
        // which resolves repositories/providers that depend on the ambient UoW.
        await using var scope = uowManager.Begin(options);
        await action(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Executes a function within a Unit of Work with automatic commit/rollback handling.
    /// Commits on success, rolls back on exception.
    /// Returns the result of the function.
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="uowManager">The unit of work manager</param>
    /// <param name="action">The function to execute</param>
    /// <param name="options">Optional UoW configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the function</returns>
    public async static Task<T> ExecuteInUowAsync<T>(
        this IUnitOfWorkManager uowManager,
        Func<CancellationToken, Task<T>> action,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Synchronous begin so the unit of work is ambient in this frame for the user action,
        // which resolves repositories/providers that depend on the ambient UoW.
        await using var scope = uowManager.Begin(options);
        var result = await action(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return result;
    }

    public async static Task<IUnitOfWork> BeginRequiresNew(this IUnitOfWorkManager uowManager, CancellationToken cancellationToken)
    {
       return await uowManager.BeginAsync(
            new UnitOfWorkOptions
            {
                IsTransactional = true,
                Scope = UnitOfWorkScopeOption.RequiresNew
            },
            cancellationToken);
    }

    /// <summary>
    /// Synchronously begins a new transactional unit of work (RequiresNew) and makes it ambient in
    /// the CALLER's execution flow. Mirrors <see cref="BeginRequiresNew(IUnitOfWorkManager, CancellationToken)"/>
    /// but uses the synchronous <see cref="IUnitOfWorkManager.Begin"/> so the ambient assignment
    /// propagates to the caller (required for programmatic/background callers whose provider-backed
    /// stores resolve their DbContext via the ambient unit of work).
    /// </summary>
    /// <param name="uowManager">The unit of work manager</param>
    /// <returns>A transactional, ambient unit of work scope</returns>
    public static IUnitOfWork BeginRequiresNew(this IUnitOfWorkManager uowManager)
    {
        return uowManager.Begin(
            new UnitOfWorkOptions
            {
                IsTransactional = true,
                Scope = UnitOfWorkScopeOption.RequiresNew
            });
    }
}

