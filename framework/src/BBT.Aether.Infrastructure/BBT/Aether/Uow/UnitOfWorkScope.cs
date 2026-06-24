using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a unit of work scope that acts as a delegating wrapper over CompositeUnitOfWork.
/// Manages ambient context and ensures proper cleanup.
/// </summary>
public sealed class UnitOfWorkScope : IEfCoreUnitOfWork
{
    private readonly CompositeUnitOfWork _root;
    private readonly IAmbientUnitOfWorkAccessor _accessor;
    private readonly IUnitOfWork? _previousAmbient;
    private readonly bool _ownsRoot;
    private IUnitOfWork? _outer;
    private bool _isDisposed;

    /// <param name="root">The composite unit of work this scope delegates to.</param>
    /// <param name="accessor">The ambient unit of work accessor.</param>
    /// <param name="ownsRoot">
    /// When <c>true</c>, this scope created the root and is responsible for disposing it on
    /// <see cref="DisposeAsync"/>. When <c>false</c> (a participating <c>Required</c> scope that
    /// shares an existing root), disposing this scope only restores ambient and must NOT tear
    /// down the shared root.
    /// </param>
    public UnitOfWorkScope(
        CompositeUnitOfWork root,
        IAmbientUnitOfWorkAccessor accessor,
        bool ownsRoot = false)
    {
        _root = root;
        _accessor = accessor;
        _ownsRoot = ownsRoot;
        _previousAmbient = accessor.Current;

        // Set this scope as the ambient unit of work
        accessor.Current = this;
    }

    /// <inheritdoc />
    public Guid Id => _root.Id;

    /// <inheritdoc />
    public UnitOfWorkOptions? Options => _root.Options;

    /// <inheritdoc />
    public IUnitOfWork? Outer => _outer;

    /// <inheritdoc />
    public bool IsAborted => _root.IsAborted;

    /// <inheritdoc />
    public bool IsCompleted => _root.IsCompleted;

    /// <inheritdoc />
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Gets the root composite unit of work.
    /// Made public to allow aspects to access the root for transaction escalation.
    /// </summary>
    public CompositeUnitOfWork Root => _root;

    /// <inheritdoc />
    public void SetOuter(IUnitOfWork? outer)
    {
        _outer = outer;
    }

    /// <inheritdoc />
    public void Abort()
    {
        _root.Abort();
    }

    /// <inheritdoc />
    public Task<TDbContext> GetDbContextAsync<TDbContext>(string schema, CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        return _root.GetDbContextAsync<TDbContext>(schema, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Delegate to root
        await _root.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _root.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // Delegate to root
        await _root.RollbackAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Restore ambient context first so the outer scope becomes current again before the
        // (owned) root is torn down. A participating Required scope (ownsRoot == false) does
        // ONLY this and must never dispose the shared root.
        _accessor.Current = _previousAmbient;

        // The owning scope is responsible for disposing the root exactly once. The root's
        // DisposeAsync is itself idempotent, but we only invoke it from the owner.
        if (_ownsRoot)
        {
            await _root.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public IDisposable OnCompleted(Func<IUnitOfWork, Task> handler)
    {
        // Forward to root so hooks fire once when root completes
        return _root.OnCompleted(handler);
    }

    /// <inheritdoc />
    public IDisposable OnFailed(Func<IUnitOfWork, Exception?, Task> handler)
    {
        // Forward to root so hooks fire once when root fails
        return _root.OnFailed(handler);
    }

    /// <inheritdoc />
    public IDisposable OnDisposed(Action<IUnitOfWork> handler)
    {
        // Forward to root so hooks fire once when root is disposed
        return _root.OnDisposed(handler);
    }
}

