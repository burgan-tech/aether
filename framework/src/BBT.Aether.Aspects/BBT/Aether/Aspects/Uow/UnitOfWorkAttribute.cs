using System;
using System.Data;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using PostSharp.Aspects;
using PostSharp.Aspects.Dependencies;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect that automatically manages Unit of Work for intercepted methods.
/// Begins a UoW before method execution, commits on success, and rolls back on exception.
/// This class can be extended to customize UoW behavior via OnBeforeAsync, OnAfterAsync, and OnExceptionAsync.
/// Execution order: Runs LAST (innermost layer, closest to method) - after Trace and Log.
/// </summary>
[PSerializable]
[AspectTypeDependency(AspectDependencyAction.Order, AspectDependencyPosition.After, typeof(TraceAttribute))]
[AspectTypeDependency(AspectDependencyAction.Order, AspectDependencyPosition.After, typeof(LogAttribute))]
[MulticastAttributeUsage(
    MulticastTargets.Method,
    AllowMultiple = false,
    Inheritance = MulticastInheritance.Strict)]
public class UnitOfWorkAttribute : AetherMethodInterceptionAspect
{
    /// <summary>
    /// Gets or sets whether the unit of work should use transactions.
    /// Default is true.
    /// </summary>
    public bool IsTransactional { get; set; } = false;

    /// <summary>
    /// Gets or sets the scope behavior for this unit of work.
    /// Default is Required (participates in existing UoW or creates new one).
    /// </summary>
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;

    /// <summary>
    /// Gets or sets the isolation level for the transaction.
    /// Default is ReadCommitted.
    /// </summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// Intercepts async method execution to wrap it in a Unit of Work.
    /// Supports prepare/initialize pattern and RequiresNew isolation via new DI scope.
    /// </summary>
    public async override Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        // Call extensibility point before starting UoW
        await OnBeforeAsync(args);

        // Get dependencies from ambient service provider
        var serviceProvider = GetServiceProvider();
        var cancellationToken = ExtractCancellationToken(args);
        var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();

        var options = CreateOptions();

        if (await uowManager.TryBeginPreparedAsync(UnitOfWorkOptions.PrepareName, options, cancellationToken))
        {
            // Prepared UoW was found and initialized
            // Just save changes - middleware will handle commit
            try
            {
                await args.ProceedAsync();

                if (uowManager.Current != null)
                {
                    await uowManager.Current.SaveChangesAsync(cancellationToken);
                    await uowManager.Current.CommitAsync(cancellationToken);
                }

                await OnAfterAsync(args);
            }
            catch (Exception ex)
            {
                if (uowManager.Current != null)
                {
                    await uowManager.Current.RollbackAsync(cancellationToken);
                }

                await OnExceptionAsync(args, ex);
                throw;
            }

            return;
        }

        // No prepared UoW, create a new one
        await using var uow = await uowManager.BeginAsync(options, cancellationToken);
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
    }

    private UnitOfWorkOptions CreateOptions()
    {
        var options = new UnitOfWorkOptions
        {
            IsTransactional = IsTransactional, Scope = Scope, IsolationLevel = IsolationLevel
        };

        return options;
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