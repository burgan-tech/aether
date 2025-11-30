namespace BBT.Aether.Uow;

/// <summary>
/// Defines the scope behavior for a unit of work.
/// </summary>
public enum UnitOfWorkScopeOption
{
    /// <summary>
    /// Participates in an existing unit of work if available, otherwise creates a new one.
    /// This is the default behavior.
    /// </summary>
    Required,

    /// <summary>
    /// Always creates a new unit of work, suspending any existing ambient unit of work.
    /// </summary>
    RequiresNew,

    /// <summary>
    /// Suppresses transactional behavior entirely.
    /// Operations execute without transaction coordination.
    /// </summary>
    Suppress
}

