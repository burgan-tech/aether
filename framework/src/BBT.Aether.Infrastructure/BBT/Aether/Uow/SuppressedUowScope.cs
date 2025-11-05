using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// A non-transactional unit of work scope that suppresses any ambient transaction.
/// Used when operations should execute without transaction coordination.
/// </summary>
public sealed class SuppressedUowScope : IUnitOfWork
{
    private readonly IAmbientUnitOfWorkAccessor _accessor;
    private readonly IUnitOfWork? _previousAmbient;

    public SuppressedUowScope(IAmbientUnitOfWorkAccessor accessor)
    {
        _accessor = accessor;
        _previousAmbient = accessor.Current;

        // Clear ambient context to suppress transaction
        accessor.Current = null;
    }

    /// <inheritdoc />
    public Guid Id => Guid.Empty;

    /// <inheritdoc />
    public bool IsAborted => false;

    /// <inheritdoc />
    public bool IsCompleted => true;

    /// <inheritdoc />
    public void Abort()
    {
        // No-op for suppressed scope
    }

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        // No-op for suppressed scope
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // No-op for suppressed scope
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Restore previous ambient context
        _accessor.Current = _previousAmbient;
        return ValueTask.CompletedTask;
    }
}

