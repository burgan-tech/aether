using System.Data;

namespace BBT.Aether.Uow;

/// <summary>
/// Options for configuring a unit of work.
/// </summary>
public class UnitOfWorkOptions
{
    /// <summary>
    /// Gets or sets whether this unit of work should use transactions.
    /// Default is false. The root transaction mode is fixed when the unit of work begins and
    /// cannot be escalated by a nested <c>Required</c> scope; use <c>RequiresNew</c> when a nested
    /// operation requires a transaction that its outer unit of work does not provide.
    /// </summary>
    public bool IsTransactional { get; set; } = false;

    /// <summary>
    /// Gets or sets the isolation level for the transaction.
    /// Default is ReadCommitted.
    /// </summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// Gets or sets the scope behavior for this unit of work.
    /// Default is Required.
    /// </summary>
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;

    /// <summary>Upper bound on distinct (DbContextType, Schema) contexts in one UnitOfWork. Guardrail.</summary>
    public int MaxDbContextCount { get; set; } = 16;
}
