using System.Data;

namespace BBT.Aether.Uow;

/// <summary>
/// Options for configuring a unit of work.
/// </summary>
public class UnitOfWorkOptions
{
    public const string PrepareName = "AetherUow";
    /// <summary>
    /// Gets or sets whether this unit of work should use transactions.
    /// Default is false (reserve pattern - transaction can be escalated later).
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
}

