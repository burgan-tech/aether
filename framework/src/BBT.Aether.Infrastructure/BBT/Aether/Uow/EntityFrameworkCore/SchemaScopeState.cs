namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Tracks the schema most recently applied on a UnitOfWork's single shared connection,
/// letting a provider's schema interceptor skip a redundant re-apply when consecutive
/// commands target the same schema. Not thread-safe by design: commands on a single
/// connection are serialized and one instance is scoped to one UnitOfWork.
/// </summary>
public sealed class SchemaScopeState
{
    public string? Current { get; set; }
}
