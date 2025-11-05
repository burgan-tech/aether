using System;
using System.Data;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using PostSharp.Aspects;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect that automatically manages Unit of Work for intercepted methods.
/// Begins a UoW before method execution, commits on success, and rolls back on exception.
/// This class can be extended to customize UoW behavior via OnBeforeAsync, OnAfterAsync, and OnExceptionAsync.
/// </summary>
[PSerializable]
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class UnitOfWorkAttribute : AetherMethodInterceptionAspect
{
    /// <summary>
    /// Gets or sets whether the unit of work should use transactions.
    /// Default is true.
    /// </summary>
    public bool IsTransactional { get; set; } = true;

    /// <summary>
    /// Gets or sets the scope behavior for this unit of work.
    /// Default is Required (participates in existing UoW or creates new one).
    /// </summary>
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;

    /// <summary>
    /// Gets or sets the isolation level for the transaction.
    /// Default is ReadCommitted.
    /// </summary>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Intercepts async method execution to wrap it in a Unit of Work.
    /// Supports lazy transaction escalation and RequiresNew isolation via new DI scope.
    /// </summary>
    public async override Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        // Call extensibility point before starting UoW
        await OnBeforeAsync(args);

        // Get dependencies from ambient service provider
        var serviceProvider = GetServiceProvider();
        var cancellationToken = ExtractCancellationToken(args);

        // Handle RequiresNew scope - create new DI scope for complete isolation
        if (Scope == UnitOfWorkScopeOption.RequiresNew)
        {
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            using var childScope = scopeFactory.CreateScope();
            var childUowManager = childScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

            var options = new UnitOfWorkOptions
            {
                IsTransactional = IsTransactional,
                Scope = UnitOfWorkScopeOption.Required, // Use Required in new scope
                IsolationLevel = IsolationLevel
            };

            await using var uow = await childUowManager.BeginAsync(options, cancellationToken);

            try
            {
                await args.ProceedAsync();
                await uow.CommitAsync(cancellationToken);
                await OnAfterAsync(args);
            }
            catch (Exception ex)
            {
                await uow.RollbackAsync(cancellationToken);
                await OnExceptionAsync(args, ex);
                throw;
            }

            return;
        }

        // Handle Required scope with potential escalation
        var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();

        if (Scope == UnitOfWorkScopeOption.Required && IsTransactional)
        {
            // Join existing UoW without starting transaction (reserve pattern)
            var joinOptions = new UnitOfWorkOptions
            {
                IsTransactional = false, // Join only, don't start transaction yet
                Scope = UnitOfWorkScopeOption.Required,
                IsolationLevel = null
            };

            await using var uow = await uowManager.BeginAsync(joinOptions, cancellationToken);

            // Escalate to transactional if we're in a UoW scope
            if (uow is UnitOfWorkScope scope && scope.Root is ITransactionalRoot root)
            {
                await root.EnsureTransactionAsync(IsolationLevel, cancellationToken);
            }

            try
            {
                await args.ProceedAsync();
                await uow.CommitAsync(cancellationToken);
                await OnAfterAsync(args);
            }
            catch (Exception ex)
            {
                await uow.RollbackAsync(cancellationToken);
                await OnExceptionAsync(args, ex);
                throw;
            }

            return;
        }

        // Default behavior (Required non-transactional or Suppress)
        var defaultOptions = new UnitOfWorkOptions
        {
            IsTransactional = IsTransactional,
            Scope = Scope,
            IsolationLevel = IsolationLevel
        };

        await using var defaultUow = await uowManager.BeginAsync(defaultOptions, cancellationToken);

        try
        {
            await args.ProceedAsync();
            await defaultUow.CommitAsync(cancellationToken);
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            await defaultUow.RollbackAsync(cancellationToken);
            await OnExceptionAsync(args, ex);
            throw;
        }
    }

    /// <summary>
    /// Intercepts synchronous method execution to wrap it in a Unit of Work.
    /// Bridges sync methods to async UoW management.
    /// </summary>
    public override void OnInvoke(MethodInterceptionArgs args)
    {
        // Bridge synchronous method to async UoW handling
        OnInvokeAsync(args).GetAwaiter().GetResult();
    }
}

