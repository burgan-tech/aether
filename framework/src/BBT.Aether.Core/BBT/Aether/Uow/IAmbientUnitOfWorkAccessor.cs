namespace BBT.Aether.Uow;

/// <summary>
/// Provides access to the ambient (current) unit of work.
/// Implemented using AsyncLocal to propagate context across async call chains.
/// </summary>
public interface IAmbientUnitOfWorkAccessor
{
    /// <summary>
    /// Gets or sets the current ambient unit of work.
    /// </summary>
    IUnitOfWork? Current { get; set; }
}

