using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a unit of work scope that acts as a delegating wrapper over CompositeUnitOfWork.
/// Manages ambient context and ensures proper cleanup.
/// Supports prepare/initialize pattern for deferred UoW activation.
/// </summary>
public sealed class UnitOfWorkScope : IUnitOfWork
{
    private readonly CompositeUnitOfWork _root;
    private readonly IAmbientUnitOfWorkAccessor _accessor;
    private readonly IUnitOfWork? _previousAmbient;

    private UnitOfWorkOptions? _options;
    private IUnitOfWork? _outer;
    private bool _isPrepared;
    private string? _preparationName;
    private bool _isDisposed;

    public UnitOfWorkScope(
        CompositeUnitOfWork root,
        IAmbientUnitOfWorkAccessor accessor)
    {
        _root = root;
        _accessor = accessor;
        _previousAmbient = accessor.Current;

        // Set this scope as the ambient unit of work
        accessor.Current = this;
    }

    /// <inheritdoc />
    public Guid Id => _root.Id;

    /// <inheritdoc />
    public UnitOfWorkOptions? Options => _options;

    /// <inheritdoc />
    public IUnitOfWork? Outer => _outer;

    /// <inheritdoc />
    public bool IsPrepared => _isPrepared;

    /// <inheritdoc />
    public string? PreparationName => _preparationName;

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
    public void Prepare(string preparationName)
    {
        if (_options is not null)
        {
            throw new InvalidOperationException("Cannot prepare an already initialized unit of work.");
        }

        _preparationName = preparationName;
        _isPrepared = true;
    }

    /// <inheritdoc />
    public void Initialize(UnitOfWorkOptions options)
    {
        if (_options is not null)
        {
            throw new InvalidOperationException("Unit of work is already initialized.");
        }

        _options = options;
        _isPrepared = false;

        // Initialize the root composite unit of work
        // This will be called by the manager when needed
    }

    /// <inheritdoc />
    public bool IsPreparedFor(string preparationName)
    {
        return _isPrepared && string.Equals(_preparationName, preparationName, StringComparison.Ordinal);
    }

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
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // No-op if still in prepared state
        if (_isPrepared)
        {
            return;
        }

        // Delegate to root
        await _root.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        // No-op if still in prepared state
        if (_isPrepared)
        {
            return;
        }

        // Delegate to root
        await _root.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // No-op if still in prepared state
        if (_isPrepared)
        {
            return;
        }

        // Delegate to root
        await _root.RollbackAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        
        // Only restore ambient context - never dispose root
        // CompositeUnitOfWork manages its own lifecycle
        _accessor.Current = _previousAmbient;
        
        return ValueTask.CompletedTask;
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

