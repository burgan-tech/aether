namespace BBT.Aether.BackgroundJob;

/// <summary>Controls how <c>EnqueueAsync</c> persists the job row relative to the caller's unit of work.</summary>
public enum JobEnqueueMode
{
    /// <summary>Default. Participate in the caller's ambient UnitOfWork when one exists — the job row
    /// commits atomically with the caller's business transaction, and a rollback discards it. When there
    /// is no ambient UnitOfWork, a short standalone transaction is used.</summary>
    Ambient = 0,

    /// <summary>Always persist in a new, independent transaction that commits immediately, regardless of any
    /// ambient UnitOfWork. The job survives even if the caller later rolls back (fire-and-forget).</summary>
    Standalone = 1,
}
