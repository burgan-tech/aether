namespace BBT.Aether.Events;

/// <summary>
/// Loss-tolerant wake nudge published directly to pub/sub (never through the outbox) after a unit
/// of work that stored at least one outbox message commits. Subscribing outbox processors treat it
/// as "poll now"; the payload is deliberately empty and delivery is best-effort — the adaptive
/// polling interval remains the safety net for lost or early signals.
/// </summary>
[EventName("aether.outbox.wakeup")]
public sealed class OutboxWakeupEvent;
