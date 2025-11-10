using System.Threading;

namespace BBT.Aether.Uow;

/// <summary>
/// AsyncLocal-based implementation of IAmbientUnitOfWorkAccessor.
/// Provides ambient context propagation across async call chains without explicit passing.
/// </summary>
public sealed class AsyncLocalAmbientUowAccessor : IAmbientUnitOfWorkAccessor
{
    private readonly static AsyncLocal<IUnitOfWork?> _current = new();

    /// <inheritdoc />
    public IUnitOfWork? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <inheritdoc />
    public IUnitOfWork? GetActiveUnitOfWork()
    {
        var uow = Current;

        // Walk the outer chain and skip prepared, completed, or disposed UoWs
        while (uow != null && (uow.IsPrepared || uow.IsCompleted || uow.IsDisposed))
        {
            uow = uow.Outer;
        }

        return uow;
    }
}

