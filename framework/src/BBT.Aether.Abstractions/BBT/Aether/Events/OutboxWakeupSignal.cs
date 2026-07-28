using System;

namespace BBT.Aether.Events;

/// <summary>
/// A hint that an outbox table may contain dispatchable rows.
/// </summary>
/// <remarks>
/// <para>
/// This is NOT a reliable message. It may be lost, duplicated, or delivered late. Losing one
/// only delays publishing by the dispatcher's fallback interval; it never loses data. The
/// outbox table remains the source of truth and fallback polling remains the reconciliation
/// mechanism.
/// </para>
/// <para>
/// Carries no business payload, no credentials and no message identifiers — only enough to
/// point a worker at the right table and partition.
/// </para>
/// </remarks>
/// <param name="Schema">The outbox schema the rows were written to.</param>
/// <param name="PartitionId">Logical partition of the written rows, or -1 meaning "check all".</param>
/// <param name="Source">Who emitted the signal. Telemetry only — never changes worker behaviour.</param>
/// <param name="EmittedAt">When the signal was emitted. Telemetry only.</param>
public sealed record OutboxWakeupSignal(
    string Schema,
    short PartitionId,
    string? Source = null,
    DateTimeOffset? EmittedAt = null)
{
    /// <summary>Sentinel <see cref="PartitionId"/> meaning "check every partition".</summary>
    public const short AllPartitions = -1;
}
