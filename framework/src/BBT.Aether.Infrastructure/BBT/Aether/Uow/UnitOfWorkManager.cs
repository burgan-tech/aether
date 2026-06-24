using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Uow;

/// <summary>
/// Manages unit of work creation and ambient propagation.
/// Implements Required, RequiresNew, and Suppress scope semantics.
/// </summary>
public sealed class UnitOfWorkManager(
    IAmbientUnitOfWorkAccessor ambient,
    IServiceProvider serviceProvider,
    AetherDomainEventOptions? domainEventOptions = null)
    : IUnitOfWorkManager
{
    /// <inheritdoc />
    public IUnitOfWork? Current => ambient.GetActiveUnitOfWork();

    /// <inheritdoc />
    /// <remarks>
    /// NOTE: The ambient assignment performed by the created scope occurs inside this async method's
    /// state machine and does NOT propagate back to the caller's execution context. After awaiting
    /// this method, <see cref="Current"/> in the caller's flow is unchanged. Use <see cref="Begin"/>
    /// when the unit of work must be ambient for subsequent provider/repository calls in the same method.
    /// </remarks>
    public async Task<IUnitOfWork> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnitOfWorkOptions();

        // Handle Suppress scope
        if (options.Scope == UnitOfWorkScopeOption.Suppress)
        {
            return new SuppressedUowScope(ambient);
        }

        var existing = ambient.GetActiveUnitOfWork() as UnitOfWorkScope;

        // Handle Required scope - participate in existing UoW if available.
        // This scope does NOT own the shared root; the scope that created it disposes it.
        if (options.Scope == UnitOfWorkScopeOption.Required && existing != null)
        {
            return new UnitOfWorkScope(
                existing.Root,
                ambient,
                ownsRoot: false);
        }

        // Create new root UoW (for RequiresNew or when no existing UoW for Required).
        // This scope owns the new root and is responsible for disposing it.
        var eventDispatcher = serviceProvider.GetService<IDomainEventDispatcher>();
        var root = new CompositeUnitOfWork(serviceProvider, eventDispatcher, domainEventOptions);
        await root.InitializeAsync(options, cancellationToken);
        return new UnitOfWorkScope(root, ambient, ownsRoot: true);
    }

    /// <inheritdoc />
    public IUnitOfWork Begin(UnitOfWorkOptions? options = null)
    {
        options ??= new UnitOfWorkOptions();

        // Handle Suppress scope
        if (options.Scope == UnitOfWorkScopeOption.Suppress)
        {
            return new SuppressedUowScope(ambient);
        }

        var existing = ambient.GetActiveUnitOfWork() as UnitOfWorkScope;

        // Handle Required scope - participate in existing UoW if available.
        // This scope does NOT own the shared root; the scope that created it disposes it.
        if (options.Scope == UnitOfWorkScopeOption.Required && existing != null)
        {
            return new UnitOfWorkScope(
                existing.Root,
                ambient,
                ownsRoot: false);
        }

        // Create new root UoW (for RequiresNew or when no existing UoW for Required).
        // Everything here is synchronous, so the `new UnitOfWorkScope(root, ambient)` ambient write
        // runs in the caller's frame and propagates into the caller's continuations.
        // This scope owns the new root and is responsible for disposing it.
        var eventDispatcher = serviceProvider.GetService<IDomainEventDispatcher>();
        var root = new CompositeUnitOfWork(serviceProvider, eventDispatcher, domainEventOptions);
        root.InitializeCore(options);
        return new UnitOfWorkScope(root, ambient, ownsRoot: true);
    }
}

