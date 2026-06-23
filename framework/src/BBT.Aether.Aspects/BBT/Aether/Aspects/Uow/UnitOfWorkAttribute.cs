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
        await OnBeforeAsync(args);

        var serviceProvider = GetServiceProvider();
        var cancellationToken = ExtractCancellationToken(args);
        var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
        var options = CreateOptions();

        // Participate in an existing ambient UnitOfWork (e.g. the request UoW the middleware owns, or an
        // outer aspect). The OWNER commits/rolls back; we only run the method and let exceptions propagate.
        if (options.Scope == UnitOfWorkScopeOption.Required && uowManager.Current is not null)
        {
            try
            {
                await args.ProceedAsync();
                await OnAfterAsync(args);
            }
            catch (Exception ex)
            {
                await OnExceptionAsync(args, ex);
                throw; // owner rolls back
            }

            return;
        }

        // Own a UnitOfWork (RequiresNew, Suppress, or Required with no ambient — e.g. non-HTTP paths). Use the
        // synchronous Begin so the unit of work is ambient in this frame and flows into ProceedAsync.
        await using var uow = uowManager.Begin(options);
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