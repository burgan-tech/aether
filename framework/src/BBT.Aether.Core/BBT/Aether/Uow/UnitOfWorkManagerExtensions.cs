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
    public static async Task ExecuteInUowAsync(
        this IUnitOfWorkManager uowManager,
        Func<CancellationToken, Task> action,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await uowManager.BeginAsync(options, cancellationToken);
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
    public static async Task<T> ExecuteInUowAsync<T>(
        this IUnitOfWorkManager uowManager,
        Func<CancellationToken, Task<T>> action,
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await uowManager.BeginAsync(options, cancellationToken);
        var result = await action(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Creates UnitOfWorkOptions for a transactional UoW that participates in existing scope or creates new one.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level, defaults to ReadCommitted</param>
    /// <returns>Configured UnitOfWorkOptions</returns>
    public static UnitOfWorkOptions RequiredTransactional(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        => new()
        {
            IsTransactional = true,
            Scope = UnitOfWorkScopeOption.Required,
            IsolationLevel = isolationLevel
        };

    /// <summary>
    /// Creates UnitOfWorkOptions for a transactional UoW that always creates a new scope.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level, defaults to ReadCommitted</param>
    /// <returns>Configured UnitOfWorkOptions</returns>
    public static UnitOfWorkOptions RequiresNewTransactional(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        => new()
        {
            IsTransactional = true,
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsolationLevel = isolationLevel
        };

    /// <summary>
    /// Creates UnitOfWorkOptions that suppresses transactional behavior.
    /// </summary>
    /// <returns>Configured UnitOfWorkOptions for suppressed scope</returns>
    public static UnitOfWorkOptions Suppressed()
        => new()
        {
            Scope = UnitOfWorkScopeOption.Suppress,
            IsTransactional = false
        };
}

