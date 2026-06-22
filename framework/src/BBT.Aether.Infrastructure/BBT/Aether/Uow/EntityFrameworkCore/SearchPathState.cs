namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Tracks the schema most recently applied via SET LOCAL search_path on a UnitOfWork's
/// single shared connection. Lets <see cref="SearchPathCommandInterceptor"/> skip the
/// redundant SET when consecutive commands target the same schema (the common case),
/// avoiding a per-command round-trip. Not thread-safe by design: commands on a single
/// Npgsql connection are serialized, and one instance is scoped to one UnitOfWork.
/// </summary>
public sealed class SearchPathState
{
    /// <summary>The schema currently applied on the shared connection, or null if none/unknown.</summary>
    public string? Current { get; set; }
}
