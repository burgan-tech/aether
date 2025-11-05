using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Uow;

/// <summary>
/// Manages unit of work creation and ambient propagation.
/// Implements Required, RequiresNew, and Suppress scope semantics.
/// </summary>
public sealed class UnitOfWorkManager(
    IAmbientUnitOfWorkAccessor ambient,
    IServiceProvider serviceProvider)
    : IUnitOfWorkManager
{
    /// <inheritdoc />
    public IUnitOfWork? Current => ambient.Current;

    /// <inheritdoc />
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

        var existing = ambient.Current as UnitOfWorkScope;

        // Handle Required scope - participate in existing UoW if available
        if (options.Scope == UnitOfWorkScopeOption.Required && existing != null)
        {
            return new UnitOfWorkScope(existing.Root, isOwner: false, ambient);
        }

        // Create new root UoW (for RequiresNew or when no existing UoW for Required)
        var sources = serviceProvider.GetServices<ILocalTransactionSource>();
        var eventDispatcher = serviceProvider.GetService<IDomainEventDispatcher>();
        var root = new CompositeUnitOfWork(sources, eventDispatcher);
        await root.InitializeAsync(options, cancellationToken);

        return new UnitOfWorkScope(root, isOwner: true, ambient);
    }
}

