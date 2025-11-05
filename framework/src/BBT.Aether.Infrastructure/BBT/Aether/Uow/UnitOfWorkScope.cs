using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a unit of work scope with ownership and participation semantics.
/// Manages ambient context and ensures proper cleanup.
/// </summary>
public sealed class UnitOfWorkScope : IUnitOfWork
{
    private readonly CompositeUnitOfWork _root;
    private readonly bool _isOwner;
    private readonly IAmbientUnitOfWorkAccessor _accessor;
    private readonly IUnitOfWork? _previousAmbient;

    public UnitOfWorkScope(
        CompositeUnitOfWork root,
        bool isOwner,
        IAmbientUnitOfWorkAccessor accessor)
    {
        _root = root;
        _isOwner = isOwner;
        _accessor = accessor;
        _previousAmbient = accessor.Current;

        // Set this scope as the ambient unit of work
        accessor.Current = this;
    }

    /// <inheritdoc />
    public Guid Id => _root.Id;

    /// <inheritdoc />
    public bool IsAborted => _root.IsAborted;

    /// <inheritdoc />
    public bool IsCompleted => _root.IsCompleted;

    /// <summary>
    /// Gets the root composite unit of work.
    /// Made public to allow aspects to access the root for transaction escalation.
    /// </summary>
    public CompositeUnitOfWork Root => _root;

    /// <inheritdoc />
    public void Abort()
    {
        _root.Abort();
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        // Only the owner can commit
        if (_isOwner)
        {
            await _root.CommitAsync(cancellationToken);
        }
        // Participants do nothing on commit - they rely on the owner
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // Only the owner can rollback
        if (_isOwner)
        {
            await _root.RollbackAsync(cancellationToken);
        }
        else
        {
            // Participants abort the root to prevent commit
            _root.Abort();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // Owner disposes the root (which auto-rolls back if not completed)
            if (_isOwner)
            {
                await _root.DisposeAsync();
            }
        }
        finally
        {
            // Restore previous ambient context
            _accessor.Current = _previousAmbient;
        }
    }
}

