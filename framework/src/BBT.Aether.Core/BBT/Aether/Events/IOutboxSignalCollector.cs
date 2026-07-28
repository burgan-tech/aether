namespace BBT.Aether.Events;

/// <summary>
/// Collects wake-up signals produced during one unit of work and publishes a coalesced set
/// after it commits.
/// </summary>
/// <remarks>
/// Scoped: one instance per unit of work. Marking is cheap and idempotent — a transaction
/// writing a hundred rows to one partition yields a single signal.
/// </remarks>
public interface IOutboxSignalCollector
{
    /// <summary>Records that a row was written to the given schema and partition.</summary>
    void Mark(string schema, short partitionId);
}
