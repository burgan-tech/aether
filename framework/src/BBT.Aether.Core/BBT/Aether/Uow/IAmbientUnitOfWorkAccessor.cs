namespace BBT.Aether.Uow;

/// <summary>
/// Provides access to the ambient (current) unit of work.
/// Implemented using AsyncLocal to propagate context across async call chains.
/// </summary>
public interface IAmbientUnitOfWorkAccessor
{
    /// <summary>
    /// Gets or sets the current ambient unit of work.
    /// This may include prepared, completed, or disposed units of work.
    /// </summary>
    IUnitOfWork? Current { get; set; }

    /// <summary>
    /// Gets the active unit of work by filtering the outer chain.
    /// Skips prepared, completed, and disposed units of work to return only active ones.
    /// </summary>
    /// <returns>The first active unit of work in the chain, or null if none exists</returns>
    IUnitOfWork? GetActiveUnitOfWork();
}

