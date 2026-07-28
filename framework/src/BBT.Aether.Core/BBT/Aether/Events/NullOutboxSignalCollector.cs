namespace BBT.Aether.Events;

/// <summary>
/// Collector used by the backward-compatible store constructor, which predates signalling.
/// Marking is a no-op; fallback polling still dispatches the rows.
/// </summary>
public sealed class NullOutboxSignalCollector : IOutboxSignalCollector
{
    public void Mark(string schema, short partitionId) { }
}
