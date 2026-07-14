using BBT.Aether.Events;

namespace BBT.Aether.Uow;

internal sealed record PendingDomainEvent(string Schema, DomainEventEnvelope Envelope);
